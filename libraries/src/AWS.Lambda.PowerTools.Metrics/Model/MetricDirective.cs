﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AWS.Lambda.PowerTools.Metrics
{
    public class MetricDirective
    {
        
        [JsonProperty("Namespace")]
        public string Namespace { get; private set; }
        
        [JsonProperty("Metrics")]
        public List<MetricDefinition> Metrics
        {
            get; private set;
        }

        [JsonIgnore]
        public List<DimensionSet> Dimensions { get; private set; }

        [JsonIgnore]
        public List<DimensionSet> DefaultDimensions {get; private set; }

        public MetricDirective() : this(null, new List<MetricDefinition>(), new List<DimensionSet>()) { }

        public MetricDirective(string metricsNamespace) : this(metricsNamespace, new List<MetricDefinition>(), new List<DimensionSet>()) { }

        public MetricDirective(string metricsNamespace, List<DimensionSet> defaultDimensions) : this(metricsNamespace, new List<MetricDefinition>(), defaultDimensions) { }
        
        private MetricDirective(string metricsNamespace, List<MetricDefinition> metrics, List<DimensionSet> defaultDimensions)
        {
            Namespace = metricsNamespace;
            Metrics = metrics;
            Dimensions = new List<DimensionSet>();
            DefaultDimensions = defaultDimensions;
        }

        [JsonProperty("Dimensions")]
        public List<List<string>> AllDimensionKeys
        {
            get
            {
                var defaultKeys = DefaultDimensions
                    .Where(d => d.DimensionKeys.Any())
                    .Select(s => s.DimensionKeys)
                    .ToList();

                var keys = Dimensions
                    .Where(d => d.DimensionKeys.Any())
                    .Select(s => s.DimensionKeys)
                    .ToList();

                defaultKeys.AddRange(keys);

                if(defaultKeys.Count == 0)
                {
                    defaultKeys.Add(new List<string>());
                }

                return defaultKeys;
            }
        }

        

        public void AddMetric(string name, double value, MetricUnit unit)
        {
            if (Metrics.Count < PowertoolsConfig.MaxMetrics)
            {
                var metric = Metrics.FirstOrDefault(metric => metric.Name == name);
                if (metric != null)
                {
                    metric.AddValue(value);
                }
                else
                {
                    Metrics.Add(new MetricDefinition(name, unit, value));
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(Metrics), "Cannot add more than 100 metrics at the same time.");
            }
        }

        internal void SetNamespace(string metricsNamespace)
        {
            Namespace = metricsNamespace;
        }

        internal void AddDimension(DimensionSet dimension)
        {
            if (Dimensions.Count < PowertoolsConfig.MaxDimensions)
            {
                var matchingKeys = AllDimensionKeys.Where(x => x.Contains(dimension.DimensionKeys[0]));
                if(!matchingKeys.Any())
                {
                    Dimensions.Add(dimension);
                }
                else
                {
                    Console.WriteLine($"WARN: Failed to Add dimension '{dimension.DimensionKeys[0]}'. Dimension already exists.");
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(Dimensions), "Cannot add more than 9 dimensions at the same time.");
            }
        }

        internal void SetDimensions(List<DimensionSet> dimensions)
        {
            Dimensions = dimensions;
        }

        internal void SetDefaultDimensions(List<DimensionSet> defaultDimensions)
        {
            if(DefaultDimensions.Count() == 0){
                DefaultDimensions = defaultDimensions;
            }
            else {
                foreach (var item in defaultDimensions)
                {                    
                    if(!DefaultDimensions.Any(d => d.DimensionKeys.Contains(item.DimensionKeys[0]))){
                        DefaultDimensions.Add(item);
                    }
                }
            }
            
        }
        
        internal Dictionary<string, string> ExpandAllDimensionSets()
        {
            var dimensions = new Dictionary<string, string>();

            foreach (DimensionSet dimensionSet in DefaultDimensions)
            {
                foreach (var dimension in dimensionSet.Dimensions)
                {
                    dimensions.TryAdd(dimension.Key, dimension.Value);
                }
            }

            foreach (DimensionSet dimensionSet in Dimensions)
            {
                foreach (var dimension in dimensionSet.Dimensions)
                {
                    dimensions.TryAdd(dimension.Key, dimension.Value);
                }
            }

            return dimensions;
        }
    }
}