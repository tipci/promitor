using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CronScheduler.Extensions.Scheduler;
using GuardNet;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Promitor.Agents.Scraper.Configuration;
using Promitor.Agents.Scraper.Discovery.Interfaces;
using Promitor.Core.Contracts;
using Promitor.Core.Extensions;
using Promitor.Core.Metrics.Interfaces;
using Promitor.Core.Metrics.Sinks;
using Promitor.Core.Scraping.Batching;
using Promitor.Core.Scraping.Configuration.Model;
using Promitor.Core.Scraping.Configuration.Model.Metrics;
using Promitor.Core.Scraping.Factories;
using Promitor.Integrations.Azure.Authentication;
using Promitor.Integrations.AzureMonitor.Configuration;
using Promitor.Integrations.LogAnalytics;

namespace Promitor.Agents.Scraper.Scheduling
{
    /// <summary>
    /// A metrics scraping job for one or more resources, either enumerated specifically or
    /// identified via resource definition groups. All metrics included are expected to have
    /// the same scraping schedule.
    /// </summary>
    public class ResourcesScrapingJob : MetricScrapingJob, IScheduledJob
    {
        private readonly MetricsDeclaration _metricsDeclaration;
        private readonly IResourceDiscoveryRepository _resourceDiscoveryRepository;
        private readonly MetricSinkWriter _metricSinkWriter;
        private readonly MetricScraperFactory _metricScraperFactory;
        private readonly IAzureScrapingSystemMetricsPublisher _azureScrapingSystemMetricsPublisher;
        private readonly AzureMonitorClientFactory _azureMonitorClientFactory;
        private readonly IMemoryCache _resourceMetricDefinitionMemoryCache;
        private readonly IScrapingMutex _scrapingTaskMutex;
        private readonly IConfiguration _configuration;
        private readonly IOptions<AzureMonitorIntegrationConfiguration> _azureMonitorIntegrationConfiguration;
        private readonly IOptions<AzureMonitorLoggingConfiguration> _azureMonitorLoggingConfiguration;
        private readonly IOptions<ConcurrencyConfiguration> _concurrencyConfiguration;
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Create a metrics scraping job for one or more resources, either enumerated specifically or
        /// identified via resource definition groups. All metrics included are expected to have
        /// the same scraping schedule.
        /// </summary>
        /// <param name="jobName">name of scheduled job</param>
        /// <param name="metricsDeclaration">declaration of which metrics to collect from which resources</param>
        /// <param name="resourceDiscoveryRepository">repository source for discovering resources via criteria</param>
        /// <param name="metricSinkWriter">destination for metric reporting output</param>
        /// <param name="metricScraperFactory">means of obtaining a metrics scraper for a particular type of resource</param>
        /// <param name="azureMonitorClientFactory">means of obtaining a Azure Monitor client</param>
        /// <param name="azureScrapingSystemMetricsPublisher">metrics collector to write metrics to Prometheus</param>
        /// <param name="resourceMetricDefinitionMemoryCache">cache of metric definitions by resource ID</param>
        /// <param name="scrapingTaskMutex">semaphore used to limit concurrency of tasks if configured, or null for no limiting</param>
        /// <param name="configuration">Promitor configuration</param>
        /// <param name="azureMonitorIntegrationConfiguration">options for Azure Monitor integration</param>
        /// <param name="azureMonitorLoggingConfiguration">options for Azure Monitor logging</param>
        /// <param name="concurrencyConfiguration">options for concurrent scrape job execution</param>
        /// <param name="loggerFactory">means to obtain a logger</param>
        /// <param name="logger">logger to use for scraping detail</param>
        public ResourcesScrapingJob(string jobName,
            MetricsDeclaration metricsDeclaration,
            IResourceDiscoveryRepository resourceDiscoveryRepository,
            MetricSinkWriter metricSinkWriter,
            MetricScraperFactory metricScraperFactory,
            AzureMonitorClientFactory azureMonitorClientFactory,
            IAzureScrapingSystemMetricsPublisher azureScrapingSystemMetricsPublisher,
            IMemoryCache resourceMetricDefinitionMemoryCache,
            IScrapingMutex scrapingTaskMutex,
            IConfiguration configuration,
            IOptions<AzureMonitorIntegrationConfiguration> azureMonitorIntegrationConfiguration,
            IOptions<AzureMonitorLoggingConfiguration> azureMonitorLoggingConfiguration,
            IOptions<ConcurrencyConfiguration> concurrencyConfiguration,
            ILoggerFactory loggerFactory,
            ILogger<ResourcesScrapingJob> logger)
            : base(jobName, logger)
        {
            Guard.NotNullOrWhitespace(jobName, nameof(jobName));
            Guard.NotNull(metricsDeclaration, nameof(metricsDeclaration));
            Guard.NotNull(metricsDeclaration.AzureMetadata, $"{nameof(metricsDeclaration)}.{nameof(metricsDeclaration.AzureMetadata)}");
            Guard.NotNull(metricsDeclaration.Metrics, $"{nameof(metricsDeclaration)}.{nameof(metricsDeclaration.Metrics)}");
            Guard.NotNull(resourceDiscoveryRepository, nameof(resourceDiscoveryRepository));
            Guard.NotNull(metricSinkWriter, nameof(metricSinkWriter));
            Guard.NotNull(metricScraperFactory, nameof(metricScraperFactory));
            Guard.NotNull(azureMonitorClientFactory, nameof(azureMonitorClientFactory));
            Guard.NotNull(azureScrapingSystemMetricsPublisher, nameof(azureScrapingSystemMetricsPublisher));
            Guard.NotNull(resourceMetricDefinitionMemoryCache, nameof(resourceMetricDefinitionMemoryCache));
            Guard.NotNull(configuration, nameof(configuration));
            Guard.NotNull(azureMonitorIntegrationConfiguration, nameof(azureMonitorIntegrationConfiguration));
            Guard.NotNull(azureMonitorLoggingConfiguration, nameof(azureMonitorLoggingConfiguration));
            Guard.NotNull(concurrencyConfiguration, nameof(azureMonitorLoggingConfiguration));
            Guard.NotNull(loggerFactory, nameof(loggerFactory));

            // all metrics must have the same scraping schedule or it is not possible for them to share the same job
            VerifyAllMetricsHaveSameScrapingSchedule(metricsDeclaration);

            _metricsDeclaration = metricsDeclaration;
            _resourceDiscoveryRepository = resourceDiscoveryRepository;
            _metricSinkWriter = metricSinkWriter;
            _metricScraperFactory = metricScraperFactory;
            _azureMonitorClientFactory = azureMonitorClientFactory;
            _azureScrapingSystemMetricsPublisher = azureScrapingSystemMetricsPublisher;
            _resourceMetricDefinitionMemoryCache = resourceMetricDefinitionMemoryCache;
            _scrapingTaskMutex = scrapingTaskMutex;
            _configuration = configuration;
            _azureMonitorIntegrationConfiguration = azureMonitorIntegrationConfiguration;
            _azureMonitorLoggingConfiguration = azureMonitorLoggingConfiguration;
            _loggerFactory = loggerFactory;
            _concurrencyConfiguration = concurrencyConfiguration;
        }

        private static void VerifyAllMetricsHaveSameScrapingSchedule(MetricsDeclaration metricsDeclaration)
        {
            if (metricsDeclaration.Metrics.Count > 1)
            {
                var metrics = metricsDeclaration.Metrics;
                var commonScraping = metrics[0].Scraping;

                for (var i = 1; i < metrics.Count; i++)
                {
                    if (!Equals(commonScraping, metrics[i].Scraping))
                        throw new ArgumentException(
                            $"The \"{metrics[i].Scraping?.Schedule}\" scraping schedule for {nameof(metricsDeclaration)}.{nameof(metricsDeclaration.Metrics)}[{i}] does not share the common scraping schedule \"{commonScraping?.Schedule}\".");
                }
            }
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Started scraping job {JobName}.", Name);

            try
            {
                var mutexReleasedAfterSeconds = _concurrencyConfiguration.Value.MutexTimeoutSeconds;
                var timeoutCancellationTokenSource = new CancellationTokenSource();
                Logger.LogInformation("Timeout after {Timeout}", mutexReleasedAfterSeconds);

                timeoutCancellationTokenSource.CancelAfter(mutexReleasedAfterSeconds * 1000);

                Logger.LogInformation("Init timeout token");
                // to enforce timeout in addition to cancellationToken passed down by .NET 
                var composedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationTokenSource.Token);
                var scrapeDefinitions = await GetAllScrapeDefinitions(composedCancellationTokenSource.Token);
                await ScrapeMetrics(scrapeDefinitions, composedCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("Cancelled scraping metrics for job {JobName}.", Name);
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "Failed to scrape metrics for job {JobName}.", Name);
            }
            finally
            {
                Logger.LogDebug("Ended scraping job {JobName}.", Name);
            }
        }

        private async Task<IReadOnlyCollection<ScrapeDefinition<IAzureResourceDefinition>>> GetAllScrapeDefinitions(CancellationToken cancellationToken)
        {
            var scrapeDefinitions = new ConcurrentBag<ScrapeDefinition<IAzureResourceDefinition>>();
            var tasks = new List<Task>();

            foreach (var metric in _metricsDeclaration.Metrics)
            {
                var metricName = metric?.PrometheusMetricDefinition?.Name ?? "Unknown";

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (metric == null)
                    {
                        throw new NullReferenceException("Metric within metrics declaration was null.");
                    }

                    if (metric.ResourceDiscoveryGroups?.Any() == true)
                    {
                        foreach (var resourceDiscoveryGroup in metric.ResourceDiscoveryGroups)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (string.IsNullOrWhiteSpace(resourceDiscoveryGroup?.Name))
                            {
                                Logger.LogError("Found resource discovery group missing a name for metric {MetricName}.", metricName);
                            }
                            else
                            {
                                await ScheduleLimitedConcurrencyAsyncTask(tasks, () => GetDiscoveryGroupScrapeDefinitions(resourceDiscoveryGroup.Name, metric, scrapeDefinitions), cancellationToken);
                            }
                        }
                    }

                    if (metric.Resources?.Any() == true)
                    {
                        foreach (var resourceDefinition in metric.Resources)
                        {
                            if (resourceDefinition == null)
                            {
                                Logger.LogError("Found null resource for metric {MetricName}.", metricName);
                            }
                            else
                            {
                                GetResourceScrapeDefinition(resourceDefinition, metric, scrapeDefinitions);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Could not get scrape definitions for metric {MetricName}.", metricName);
                }
            }

            await Task.WhenAll(tasks);
            return scrapeDefinitions;
        }

        private async Task GetDiscoveryGroupScrapeDefinitions(string resourceDiscoveryGroupName, MetricDefinition metricDefinition, ConcurrentBag<ScrapeDefinition<IAzureResourceDefinition>> scrapeDefinitions)
        {
            // this runs in a separate thread, must trap exceptions
            try
            {
                Logger.LogInformation("Scraping resource collection {ResourceDiscoveryGroup}.", resourceDiscoveryGroupName);

                var discoveredResources = await _resourceDiscoveryRepository.GetResourceDiscoveryGroupAsync(resourceDiscoveryGroupName);
                if (discoveredResources == null)
                {
                    Logger.LogWarning("Discovered no resources for resource collection {ResourceDiscoveryGroup}.", resourceDiscoveryGroupName);
                    return;
                }

                Logger.LogInformation("Discovered {ResourceCount} resources for resource collection {ResourceDiscoveryGroup}.", discoveredResources.Count, resourceDiscoveryGroupName);

                foreach (var discoveredResource in discoveredResources)
                {
                    Logger.LogDebug("Discovered resource {DiscoveredResource}.", discoveredResource);
                    var scrapeDefinition = metricDefinition.CreateScrapeDefinition(discoveredResource, _metricsDeclaration.AzureMetadata);
                    scrapeDefinitions.Add(scrapeDefinition);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to discover resources for group {GroupName}.", resourceDiscoveryGroupName);
            }
        }

        private void GetResourceScrapeDefinition(IAzureResourceDefinition resourceDefinition, MetricDefinition metricDefinition, ConcurrentBag<ScrapeDefinition<IAzureResourceDefinition>> scrapeDefinitions)
        {
            var scrapeDefinition = metricDefinition.CreateScrapeDefinition(resourceDefinition, _metricsDeclaration.AzureMetadata);
            scrapeDefinitions.Add(scrapeDefinition);
        }

        private async Task ScrapeMetrics(IEnumerable<ScrapeDefinition<IAzureResourceDefinition>> scrapeDefinitions, CancellationToken cancellationToken)
        {   
            var tasks = new List<Task>();
            var batchScrapingEnabled = this._azureMonitorIntegrationConfiguration.Value.MetricsBatching?.Enabled ?? false;
            if (batchScrapingEnabled) {
                Logger.LogInformation("Promitor Scraper with operate in batch scraping mode, with max batch size {BatchSize}", this._azureMonitorIntegrationConfiguration.Value.MetricsBatching.MaxBatchSize);
                Logger.LogWarning("Batch scraping is an experimental feature. See Promitor.io for its limitations and cost considerations");

                var batchScrapeDefinitions = AzureResourceDefinitionBatching.GroupScrapeDefinitions(scrapeDefinitions, this._azureMonitorIntegrationConfiguration.Value.MetricsBatching.MaxBatchSize);

                foreach(var batchScrapeDefinition in batchScrapeDefinitions) {
                    var azureMetricName = batchScrapeDefinition.ScrapeDefinitionBatchProperties.AzureMetricConfiguration.MetricName;
                    var resourceType = batchScrapeDefinition.ScrapeDefinitionBatchProperties.ResourceType;
                    Logger.LogInformation("Executing batch scrape job of size {BatchSize} for Azure Metric {AzureMetricName} for resource type {ResourceType}.", batchScrapeDefinition.ScrapeDefinitions.Count, azureMetricName, resourceType);
                    await ScheduleLimitedConcurrencyAsyncTask(tasks, () => ScrapeMetricBatched(batchScrapeDefinition), cancellationToken);
                }
            } else {
                foreach (var scrapeDefinition in scrapeDefinitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var metricName = scrapeDefinition.PrometheusMetricDefinition.Name;
                    var resourceType = scrapeDefinition.Resource.ResourceType;
                    Logger.LogInformation("Scraping {MetricName} for resource type {ResourceType}.", metricName, resourceType);

                    await ScheduleLimitedConcurrencyAsyncTask(tasks, () => ScrapeMetric(scrapeDefinition), cancellationToken);
                }
            }

            await Task.WhenAll(tasks);
        }
        private async Task ScrapeMetricBatched(BatchScrapeDefinition<IAzureResourceDefinition> batchScrapeDefinition) {
            try
            {   
                var resourceSubscriptionId = batchScrapeDefinition.ScrapeDefinitionBatchProperties.SubscriptionId;
                var azureMonitorClient = _azureMonitorClientFactory.CreateIfNotExists(_metricsDeclaration.AzureMetadata, _metricsDeclaration.AzureMetadata.TenantId,
                    resourceSubscriptionId, _metricSinkWriter, _azureScrapingSystemMetricsPublisher, _resourceMetricDefinitionMemoryCache, _configuration,
                    _azureMonitorIntegrationConfiguration, _azureMonitorLoggingConfiguration, _loggerFactory);
                var azureEnvironment = _metricsDeclaration.AzureMetadata.GetAzureEnvironment();

                var tokenCredential = AzureAuthenticationFactory.GetTokenCredential(azureEnvironment.ManagementEndpoint, _metricsDeclaration.AzureMetadata.TenantId,
                    AzureAuthenticationFactory.GetConfiguredAzureAuthentication(_configuration), new Uri (_metricsDeclaration.AzureMetadata.GetAzureEnvironment().AuthenticationEndpoint));
                var logAnalyticsClient = new LogAnalyticsClient(_loggerFactory, _metricsDeclaration.AzureMetadata.GetLogAnalyticsEndpoint(), tokenCredential);

                var scraper = _metricScraperFactory.CreateScraper(batchScrapeDefinition.ScrapeDefinitionBatchProperties.ResourceType, _metricSinkWriter, _azureScrapingSystemMetricsPublisher, azureMonitorClient, logAnalyticsClient);
                
                await scraper.BatchScrapeAsync(batchScrapeDefinition);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to scrape metric {MetricName} for resource batch {ResourceName}. Details: {Details}",
                    batchScrapeDefinition.ScrapeDefinitionBatchProperties.PrometheusMetricDefinition.Name, batchScrapeDefinition.ScrapeDefinitionBatchProperties.ResourceType, ex.ToString());
            }
        }

        private async Task ScrapeMetric(ScrapeDefinition<IAzureResourceDefinition> scrapeDefinition)
        {
            // this runs in a separate thread, must trap exceptions
            try
            {
                var resourceSubscriptionId = !string.IsNullOrWhiteSpace(scrapeDefinition.Resource.SubscriptionId)
                    ? scrapeDefinition.Resource.SubscriptionId
                    : _metricsDeclaration.AzureMetadata.SubscriptionId;
                var azureEnvironent = _metricsDeclaration.AzureMetadata.GetAzureEnvironment();
                Logger.LogInformation("Parsed SDK Config {UseAzureMonitorSdk}", _azureMonitorIntegrationConfiguration.Value.UseAzureMonitorSdk);
                var azureMonitorClient = _azureMonitorClientFactory.CreateIfNotExists(_metricsDeclaration.AzureMetadata, _metricsDeclaration.AzureMetadata.TenantId,
                    resourceSubscriptionId, _metricSinkWriter, _azureScrapingSystemMetricsPublisher, _resourceMetricDefinitionMemoryCache, _configuration,
                    _azureMonitorIntegrationConfiguration, _azureMonitorLoggingConfiguration, _loggerFactory);

                var tokenCredential = AzureAuthenticationFactory.GetTokenCredential(azureEnvironent.ManagementEndpoint, _metricsDeclaration.AzureMetadata.TenantId,
                    AzureAuthenticationFactory.GetConfiguredAzureAuthentication(_configuration), new Uri(_metricsDeclaration.AzureMetadata.GetAzureEnvironment().AuthenticationEndpoint));
                var logAnalyticsClient = new LogAnalyticsClient(_loggerFactory, _metricsDeclaration.AzureMetadata.GetLogAnalyticsEndpoint(), tokenCredential);

                var scraper = _metricScraperFactory.CreateScraper(scrapeDefinition.Resource.ResourceType, _metricSinkWriter, _azureScrapingSystemMetricsPublisher, azureMonitorClient, logAnalyticsClient);
                
                await scraper.ScrapeAsync(scrapeDefinition);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to scrape metric {MetricName} for resource {ResourceName}.",
                    scrapeDefinition.PrometheusMetricDefinition.Name, scrapeDefinition.Resource.ResourceName);
            }
        }

        /// <summary>
        /// Run some task work in the thread pool, but only allow a limited number of threads to go at a time
        /// (unless max degree of parallelism wasn't configured, in which case mutex is null and no limit is imposed). 
        /// </summary>
        private async Task ScheduleLimitedConcurrencyAsyncTask(ICollection<Task> tasks, Func<Task> asyncWork, CancellationToken cancellationToken)
        {
            if (_scrapingTaskMutex == null)
            {
                tasks.Add(Task.Run(asyncWork, cancellationToken));
                return;
            }

            await _scrapingTaskMutex.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(() => WorkWrapper(asyncWork, cancellationToken), cancellationToken));
        }

        private async Task WorkWrapper(Func<Task> work, CancellationToken cancellationToken)
        {
            try
            {
                var tcs = new TaskCompletionSource<object>();
                cancellationToken.Register(() => {
                    tcs.TrySetResult(null);
                });
                var completedTask = await Task.WhenAny(work(), tcs.Task);
                if (completedTask == tcs.Task)
                {
                    Logger.LogError("Scrape job {JobName} was cancelled due to timeout. However, dangling async tasks " +
                          "may be running for an unbounded amount of time. In the rare case where " +
                          "many such timeouts occur, consider restarting the Scraper Agent or tuning the mutex timeout configuration.", Name);
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            finally
            {
                Logger.LogWarning("release lock");
                _scrapingTaskMutex.Release();
            }
        }
    }
}
