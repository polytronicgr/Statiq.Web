﻿using System.Collections.Generic;
using System.Linq;
using Statiq.Common;
using Statiq.Core;
using Statiq.Web.Modules;
using Statiq.Yaml;

namespace Statiq.Web.Pipelines
{
    public class Data : Pipeline
    {
        public Data()
        {
            Dependencies.Add(nameof(DirectoryMetadata));

            InputModules = new ModuleList
            {
                new ReadFiles(Config.FromSetting<IEnumerable<string>>(WebKeys.DataFiles))
            };

            ProcessModules = new ModuleList
            {
                // Concat all documents from externally declared dependencies (exclude explicit dependencies above)
                new ConcatDocuments(Config.FromContext<IEnumerable<IDocument>>(ctx => ctx.Outputs.FromPipelines(ctx.Pipeline.GetAllDependencies(ctx).Except(Dependencies).ToArray()))),

                // Apply directory metadata
                new ApplyDirectoryMetadata(),

                // Parse the content into metadata depending on the content type
                new ExecuteSwitch(Config.FromDocument(doc => doc.ContentProvider.MediaType))
                    .Case(MediaTypes.Json, new ParseJson())
                    .Case(MediaTypes.Yaml, new ParseYaml()),

                // Filter out excluded documents
                new FilterDocuments(Config.FromDocument(doc => !doc.GetBool(WebKeys.Excluded))),

                // Filter out feed documents (they'll get processed by the Feed pipeline)
                new FilterDocuments(Config.FromDocument(doc => !Feeds.IsFeed(doc))),

                new SetDestination(),
                new ExecuteIf(Config.FromSetting(WebKeys.OptimizeDataFileNames, true))
                {
                    new OptimizeFileName()
                }
            };

            OutputModules = new ModuleList
            {
                new FilterDocuments(Config.FromDocument<bool>(WebKeys.ShouldOutput)),
                new WriteFiles()
            };
        }
    }
}
