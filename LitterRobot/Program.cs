﻿using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LitterRobot.DataAccess;
using LitterRobot.Managers;
using LitterRobot.Models.Shared;
using TwoMQTT.Core;
using TwoMQTT.Core.DataAccess;
using TwoMQTT.Core.Extensions;
using System;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Collections.Generic;

namespace LitterRobot
{
    class Program : ConsoleProgram
    {
        static async Task Main(string[] args)
        {
            var p = new Program();
            p.MapOldEnvVariables();
            await p.ExecuteAsync(args);
        }

        protected override IServiceCollection ConfigureServices(HostBuilderContext hostContext, IServiceCollection services)
        {
            var sharedSect = hostContext.Configuration.GetSection(Models.Shared.Opts.Section);
            var sourceSect = hostContext.Configuration.GetSection(Models.SourceManager.Opts.Section);
            var sinkSect = hostContext.Configuration.GetSection(Models.SinkManager.Opts.Section);

            services.AddHttpClient<SourceManager>();

            services.AddMemoryCache();
            services.AddHttpClient<IHTTPSourceDAO<SlugMapping, Command, Models.SourceManager.FetchResponse, object>>();
            services.AddTransient<IHTTPSourceDAO<SlugMapping, Command, Models.SourceManager.FetchResponse, object>, SourceDAO>();

            return services
                .Configure<Models.Shared.Opts>(sharedSect)
                .Configure<Models.SourceManager.Opts>(sourceSect)
                .Configure<Models.SinkManager.Opts>(sinkSect)
                .ConfigureBidirectionalSourceSink<Resource, Command, SourceManager, SinkManager>();
        }

        [Obsolete("Remove in the near future.")]
        private void MapOldEnvVariables()
        {
            var found = false;
            var foundOld = new List<string>();
            var mappings = new[]
            {
                new { Src = "LITTERROBOT_EMAIL", Dst = "LITTERROBOT__LOGIN", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "LITTERROBOT_PASSWORD", Dst = "LITTERROBOT__PASSWORD", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "LITTERROBOT_KNOWN", Dst = "LITTERROBOT__RESOURCES", CanMap = true, Strip = "", Split = ",", Sep = ":" },
                new { Src = "LITTERROBOT_LOOKUPINTERVAL", Dst = "LITTERROBOT__POLLINGINTERVAL", CanMap = false, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_TOPICPREFIX", Dst = "LITTERROBOT__MQTT__TOPICPREFIX", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_DISCOVERY", Dst = "LITTERROBOT__MQTT__DISCOVERYENABLED", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_DISCOVERYPREFIX", Dst = "LITTERROBOT__MQTT__DISCOVERYPREFIX", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_DISCOVERYNAME", Dst = "LITTERROBOT__MQTT__DISCOVERYNAME", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_BROKER", Dst = "LITTERROBOT__MQTT__BROKER", CanMap = true, Strip = "tcp://", Split = "", Sep = "" },
                new { Src = "MQTT_USERNAME", Dst = "LITTERROBOT__MQTT__USERNAME", CanMap = true, Strip = "", Split = "", Sep = "" },
                new { Src = "MQTT_PASSWORD", Dst = "LITTERROBOT__MQTT__PASSWORD", CanMap = true, Strip = "", Split = "", Sep = "" },
            };

            foreach (var mapping in mappings)
            {
                var old = Environment.GetEnvironmentVariable(mapping.Src);
                if (string.IsNullOrEmpty(old))
                {
                    continue;
                }

                found = true;
                foundOld.Add($"{mapping.Src} => {mapping.Dst}");

                if (!mapping.CanMap)
                {
                    continue;
                }

                // Strip junk where possible
                if (!string.IsNullOrEmpty(mapping.Strip))
                {
                    old = old.Replace(mapping.Strip, string.Empty);
                }

                // Simple
                if (string.IsNullOrEmpty(mapping.Split))
                {
                    Environment.SetEnvironmentVariable(mapping.Dst, old);
                }
                // Complex
                else
                {
                    var resourceSlugs = old.Split(mapping.Split);
                    var i = 0;
                    foreach (var resourceSlug in resourceSlugs)
                    {
                        var parts = resourceSlug.Split(mapping.Sep);
                        var id = parts.Length >= 1 ? parts[0] : string.Empty;
                        var slug = parts.Length >= 2 ? parts[1] : string.Empty;
                        var idEnv = $"{mapping.Dst}__{i}__LRID";
                        var slugEnv = $"{mapping.Dst}__{i}__Slug";
                        Environment.SetEnvironmentVariable(idEnv, id);
                        Environment.SetEnvironmentVariable(slugEnv, slug);
                    }
                }

            }


            if (found)
            {
                var loggerFactory = LoggerFactory.Create(builder => { builder.AddConsole(); });
                var logger = loggerFactory.CreateLogger<Program>();
                logger.LogWarning("Found old environment variables.");
                logger.LogWarning($"Please migrate to the new environment variables: {(string.Join(", ", foundOld))}");
                Thread.Sleep(5000);
            }
        }
    }
}