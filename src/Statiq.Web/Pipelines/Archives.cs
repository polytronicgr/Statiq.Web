﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Statiq.App;
using Statiq.Common;
using Statiq.Core;
using Statiq.Html;
using Statiq.Markdown;
using Statiq.Razor;
using Statiq.Web.Modules;
using Statiq.Yaml;

namespace Statiq.Web.Pipelines
{
    public class Archives : Pipeline
    {
        public Archives(Templates templates)
        {
            Dependencies.AddRange(nameof(Content), nameof(Data));

            InputModules = new ModuleList
            {
                new ReadFiles(Config.FromSetting<IEnumerable<string>>(WebKeys.ContentFiles))
            };

            ProcessModules = new ModuleList
            {
                // Process front matter and sidecar files
                new ProcessMetadata(),

                // Filter just to archive documents
                new FilterDocuments(Config.FromDocument(IsArchive)),

                new ForEachDocument
                {
                    new ExecuteConfig(Config.FromDocument(archiveDoc =>
                    {
                        ModuleList modules = new ModuleList();

                        // Get outputs from the pipeline(s)
                        modules.Add(
                            new ReplaceDocuments(archiveDoc.GetList(WebKeys.ArchivePipelines, new[] { nameof(Content) }).ToArray()),
                            new FlattenTree(),
                            new MergeMetadata(Config.FromValue(archiveDoc.Yield())).KeepExisting());

                        // Filter by document source
                        if (archiveDoc.ContainsKey(WebKeys.ArchiveSources))
                        {
                            modules.Add(new FilterSources(archiveDoc.GetList<string>(WebKeys.ArchiveSources)));
                        }

                        // Filter by metadata
                        if (archiveDoc.ContainsKey(WebKeys.ArchiveFilter))
                        {
                            modules.Add(new FilterDocuments(Config.FromDocument(doc => doc.GetBool(WebKeys.ArchiveFilter))));
                        }

                        // Order the documents
                        if (archiveDoc.ContainsKey(WebKeys.ArchiveOrderKey))
                        {
                            modules.Add(
                                new OrderDocuments(archiveDoc.GetString(WebKeys.ArchiveOrderKey))
                                    .Descending(archiveDoc.GetBool(WebKeys.ArchiveOrderDescending)));
                        }

                        // Are we making groups?
                        if (archiveDoc.ContainsKey(WebKeys.ArchiveKey))
                        {
                            // Group by the archive key
                            string archiveKey = archiveDoc.GetRaw(WebKeys.ArchiveKey) as string;
                            modules.Add(
                                new GroupDocuments(Config.FromDocument(doc => doc.GetList(archiveKey ?? WebKeys.ArchiveKey, new object[] { })))
                                        .WithSource(archiveDoc.Source),
                                new MergeDocuments(Config.FromValue(archiveDoc.Yield())).KeepExistingMetadata());

                            // Paginate the groups
                            ModuleList paginateGroups = new ModuleList();
                            if (archiveDoc.ContainsKey(WebKeys.ArchivePageSize))
                            {
                                paginateGroups.Add(new ForEachDocument
                                {
                                    // Paginate the group
                                    new ExecuteConfig(Config.FromDocument(groupDoc => new ModuleList
                                    {
                                        new ReplaceDocuments(Config.FromDocument<IEnumerable<IDocument>>(doc => doc.GetChildren())),
                                        new PaginateDocuments(archiveDoc.GetInt(WebKeys.ArchivePageSize))
                                            .WithSource(archiveDoc.Source),
                                        new MergeMetadata(Config.FromValue(groupDoc.Yield())).KeepExisting(),
                                        new MergeDocuments(Config.FromValue(archiveDoc.Yield())).KeepExistingMetadata()
                                    }))
                                });
                            }
                            paginateGroups.Add(GetTitleAndDestinationModules(archiveDoc));

                            // Create a top-level index
                            // Make sure the pre-paginated group documents get a title and destination in case something references them
                            // (they'll end up pointing to the first page if the group was paginated)
                            ModuleList topLevel = new ModuleList(
                                GetTitleAndDestinationModules(archiveDoc));
                            topLevel.Add(GetTopLevelIndexModules(archiveDoc));

                            // Add the group modules and create a top-level index document in a branch
                            modules.Add(new ExecuteBranch(paginateGroups).Branch(topLevel));
                        }
                        else
                        {
                            // We weren't grouping so now we've got a sequence of documents

                            // Paginate the documents
                            if (archiveDoc.ContainsKey(WebKeys.ArchivePageSize))
                            {
                                modules.Add(
                                    new PaginateDocuments(archiveDoc.GetInt(WebKeys.ArchivePageSize))
                                        .WithSource(archiveDoc.Source),
                                    new MergeDocuments(Config.FromValue(archiveDoc.Yield())).KeepExistingMetadata());
                                modules.Add(GetTitleAndDestinationModules(archiveDoc));
                            }
                            else
                            {
                                // We didn't make pages so create a top-level document
                                modules.Add(GetTopLevelIndexModules(archiveDoc));
                            }
                        }

                        // Render any markdown content
                        modules.Add(new RenderProcessTemplates(templates));

                        return modules;
                    }))
                },
            };

            PostProcessModules = new ModuleList
            {
                new RenderPostProcessTemplates(templates)
            };

            OutputModules = new ModuleList
            {
                new WriteFiles()
            };
        }

        private static IModule[] GetTopLevelIndexModules(IDocument archiveDoc) => new IModule[]
        {
            new ReplaceDocuments(Config.FromContext(ctx => archiveDoc.Clone(new MetadataItems { { Keys.Children, ctx.Inputs } }).Yield())),
            new AddTitle(),
            new SetDestination(Config.FromValue(archiveDoc.Destination.ChangeExtension(".html")), true)
        };

        private static IModule[] GetTitleAndDestinationModules(IDocument archiveDoc) => new IModule[]
        {
            new AddTitle(
                Config.FromDocument(doc =>
                {
                    if (doc.ContainsKey(WebKeys.ArchiveTitle))
                    {
                        return doc.GetString(WebKeys.ArchiveTitle);
                    }

                    // Default title
                    string title = doc.GetString(Keys.Title);
                    if (doc.ContainsKey(Keys.GroupKey))
                    {
                        title += " - " + doc.GetString(Keys.GroupKey);
                    }
                    int index = doc.GetInt(Keys.Index);
                    return index <= 1 ? title : (title + $" (Page {index})");
                })).KeepExisting(false),
            new SetDestination(
                Config.FromDocument(doc =>
                {
                    if (doc.ContainsKey(WebKeys.ArchiveDestination))
                    {
                        return doc.GetPath(WebKeys.ArchiveDestination);
                    }

                    // Default destination
                    NormalizedPath destination = archiveDoc.Destination.ChangeExtension(null);
                    if (doc.ContainsKey(Keys.GroupKey))
                    {
                        destination.Combine(NormalizedPath.ReplaceInvalidFileNameChars(doc.GetString(Keys.GroupKey)));
                    }
                    int index = doc.GetInt(Keys.Index);
                    if (index > 1)
                    {
                        destination = destination.Combine(index.ToString());
                    }
                    return destination.AppendExtension(".html");
                }),
                true)
        };

        public static bool IsArchive(IDocument document) =>
            document.ContainsKey(WebKeys.ArchiveSources) || document.ContainsKey(WebKeys.ArchiveFilter) || document.ContainsKey(WebKeys.ArchiveKey);
    }
}
