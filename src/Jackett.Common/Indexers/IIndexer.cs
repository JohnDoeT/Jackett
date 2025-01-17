﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Newtonsoft.Json.Linq;

namespace Jackett.Common.Indexers
{
    public class IndexerResult
    {
        public IIndexer Indexer { get; set; }
        public IEnumerable<ReleaseInfo> Releases { get; set; }

        public IndexerResult(IIndexer Indexer, IEnumerable<ReleaseInfo> Releases)
        {
            this.Indexer = Indexer;
            this.Releases = Releases;
        }
    }

    public interface IIndexer
    {
        string SiteLink { get; }
        string[] AlternativeSiteLinks { get; }

        string DisplayName { get; }
        string DisplayDescription { get; }
        string Type { get; }
        string Language { get; }
        string LastError { get; set; }
        string ID { get; }
        Encoding Encoding { get; }

        TorznabCapabilities TorznabCaps { get; }

        // Whether this indexer has been configured, verified and saved in the past and has the settings required for functioning
        bool IsConfigured { get; }

        // Retrieved for starting setup for the indexer via web API
        Task<ConfigurationData> GetConfigurationForSetup();

        // Called when web API wants to apply setup configuration via web API, usually this is where login and storing cookie happens
        Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson);

        // Called on startup when initializing indexers from saved configuration
        void LoadFromSavedConfiguration(JToken jsonConfig);

        void SaveConfig();

        void Unconfigure();

        Task<IndexerResult> ResultsForQuery(TorznabQuery query);

        bool CanHandleQuery(TorznabQuery query);

        List<CategoryMapping> GetAllCategoryMappings();
    }

    public interface IWebIndexer : IIndexer
    {
        Task<byte[]> Download(Uri link);
    }
}
