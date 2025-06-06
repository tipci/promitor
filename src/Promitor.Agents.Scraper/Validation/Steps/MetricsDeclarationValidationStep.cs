﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Promitor.Agents.Core.Validation.Interfaces;
using Promitor.Agents.Core.Validation.Steps;
using Promitor.Core.Scraping.Configuration.Model;
using Promitor.Core.Scraping.Configuration.Model.Metrics;
using Promitor.Core.Scraping.Configuration.Providers.Interfaces;
using Promitor.Core.Scraping.Configuration.Serialization;
using Promitor.Agents.Scraper.Validation.MetricDefinitions;
using ValidationResult = Promitor.Agents.Core.Validation.ValidationResult;
using Promitor.Core.Serialization.Enum;
using Promitor.Core.Contracts;

namespace Promitor.Agents.Scraper.Validation.Steps
{
    public class MetricsDeclarationValidationStep : ValidationStep, IValidationStep
    {
        private readonly IMetricsDeclarationProvider _metricsDeclarationProvider;
        
        public MetricsDeclarationValidationStep(IMetricsDeclarationProvider metricsDeclarationProvider, ILogger<MetricsDeclarationValidationStep> logger) : base( logger)
        {
            _metricsDeclarationProvider = metricsDeclarationProvider;
        }

        public string ComponentName => "Metrics Declaration";

        public ValidationResult Run()
        {
            var errorReporter = new ErrorReporter();
            var metricsDeclaration = _metricsDeclarationProvider.Get(applyDefaults: true, errorReporter: errorReporter);
            if (metricsDeclaration == null)
            {
                return ValidationResult.Failure(ComponentName, "Unable to deserialize configured metrics declaration");
            }

            LogDeserializationMessages(errorReporter);

            if (errorReporter.HasErrors)
            {
                return ValidationResult.Failure(ComponentName, "Errors were found while deserializing the metric configuration.");
            }

            var validationErrors = new List<string>();
            var azureMetadataErrorMessages = ValidateAzureMetadata(metricsDeclaration.AzureMetadata, metricsDeclaration.Metrics);
            validationErrors.AddRange(azureMetadataErrorMessages);

            var metricDefaultErrorMessages = ValidateMetricDefaults(metricsDeclaration.MetricDefaults);
            validationErrors.AddRange(metricDefaultErrorMessages);

            var metricsErrorMessages = ValidateMetrics(metricsDeclaration.Metrics, metricsDeclaration.MetricDefaults);
            validationErrors.AddRange(metricsErrorMessages);

            return validationErrors.Any() ? ValidationResult.Failure(ComponentName, validationErrors) : ValidationResult.Successful(ComponentName);
        }

        private void LogDeserializationMessages(IErrorReporter errorReporter)
        {
            if (errorReporter.Messages.Any())
            {
                var combinedMessages = string.Join(
                    Environment.NewLine, errorReporter.Messages.Select(message => message.FormattedMessage));

                var deserializationProblemsMessage = $"The following problems were found with the metric configuration:{Environment.NewLine}{combinedMessages}";
                if (errorReporter.HasErrors)
                {
                    Logger.LogError(deserializationProblemsMessage);
                }
                else
                {
                    Logger.LogWarning(deserializationProblemsMessage);
                }
            }
        }
        
        private static IEnumerable<string> ValidateMetricDefaults(MetricDefaults metricDefaults)
        {
            if (string.IsNullOrWhiteSpace(metricDefaults.Scraping?.Schedule))
            {
                yield return @"No default metric scraping schedule is defined.";
            }

            if (metricDefaults.Limit > Promitor.Core.Defaults.MetricDefaults.Limit)
            {
                yield return $"Limit cannot be higher than {Promitor.Core.Defaults.MetricDefaults.Limit}";
            }

            if (metricDefaults.Limit <= 0)
            {
                yield return @"Limit has to be at least 1";
            }
        }

        private static IEnumerable<string> DetectDuplicateMetrics(List<MetricDefinition> metrics)
        {
            var duplicateMetricNames = metrics.GroupBy(metric => metric.PrometheusMetricDefinition?.Name)
                .Where(groupedMetrics => groupedMetrics.Count() > 1)
                .Select(groupedMetrics => groupedMetrics.Key);

            return duplicateMetricNames;
        }

        private static IEnumerable<string> ValidateAzureMetadata(AzureMetadata azureMetadata, List<MetricDefinition> metrics)
        {
            var errorMessages = new List<string>();

            if (azureMetadata == null)
            {
                errorMessages.Add("No azure metadata is configured");
                return errorMessages;
            }

            if (string.IsNullOrWhiteSpace(azureMetadata.TenantId))
            {
                errorMessages.Add("No tenant id is configured");
            }

            if (string.IsNullOrWhiteSpace(azureMetadata.SubscriptionId))
            {
                errorMessages.Add("No subscription id is configured");
            }

            if (string.IsNullOrWhiteSpace(azureMetadata.ResourceGroupName))
            {
                errorMessages.Add("No resource group name is configured");
            }

            if (azureMetadata.Cloud == AzureCloud.Custom)
            {
                errorMessages.AddRange(ValidateCustomCloud(azureMetadata, metrics));
            }

            return errorMessages;
        }

        private static IEnumerable<string> ValidateMetrics(List<MetricDefinition> metrics, MetricDefaults metricDefaults)
        {
            var errorMessages = new List<string>();

            if (metrics == null)
            {
                errorMessages.Add("No metrics are configured");
                return errorMessages;
            }

            var metricsValidator = new MetricsValidator(metricDefaults);
            var metricErrorMessages = metricsValidator.Validate(metrics);
            errorMessages.AddRange(metricErrorMessages);

            // Detect duplicate metric names
            var duplicateMetrics = DetectDuplicateMetrics(metrics);
            errorMessages.AddRange(duplicateMetrics.Select(duplicateMetricName => $"Metric name '{duplicateMetricName}' is declared multiple times"));

            return errorMessages;
        }

        private static IEnumerable<string> ValidateCustomCloud(AzureMetadata azureMetadata, List<MetricDefinition> metrics)
        {
            var errorMessages = new List<string>();

            if (azureMetadata.Endpoints == null)
            {
                errorMessages.Add("Endpoints are not configured for Azure Custom cloud");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.AuthenticationEndpoint))
                {
                    errorMessages.Add("Azure Custom cloud authentication endpoint was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.ResourceManagerEndpoint))
                {
                    errorMessages.Add("Azure Custom cloud resource management endpoint was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.ManagementEndpoint))
                {
                    errorMessages.Add("Azure Custom cloud service management endpoint was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.GraphEndpoint))
                {
                    errorMessages.Add("Azure Custom cloud graph endpoint was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.StorageEndpointSuffix))
                {
                    errorMessages.Add("Azure Custom cloud storage service url suffix was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.KeyVaultSuffix))
                {
                    errorMessages.Add("Azure Custom cloud Key Vault service url suffix was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.MetricsClientAudience))
                {
                    errorMessages.Add("Azure Custom cloud metric client audiences endpoint was not configured to query");
                }
                if (string.IsNullOrWhiteSpace(azureMetadata.Endpoints.MetricsQueryAudience))
                {
                    errorMessages.Add("Azure Custom cloud metric query audiences endpoint was not configured to query");
                }

                var usesLogAnalytics = metrics?.Any(m => m.ResourceType == ResourceType.LogAnalytics) ?? false;
                if (usesLogAnalytics && string.IsNullOrWhiteSpace(azureMetadata.Endpoints.LogAnalyticsEndpoint))
                {
                    errorMessages.Add("Azure Custom cloud Log Analytics endpoint was not configured when Log Analytics resource type was used");
                }
            }

            return errorMessages;
        }
    }
}