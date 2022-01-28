﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.AspNetCore.Hosting;
using Microsoft.Diagnostics.Monitoring.TestCommon;
using Microsoft.Diagnostics.Tools.Monitor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Diagnostics.Monitoring.Tool.UnitTests
{
    public sealed class ConfigurationTests
    {
        private static readonly Dictionary<string, string> AppSettingsContent = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.AppSettings) }
        };

        private static readonly Dictionary<string, string> AspnetEnvironmentVariables = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.AspnetEnvironment) }
        };

        private static readonly Dictionary<string, string> DotnetEnvironmentVariables = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.DotnetEnvironment) }
        };

        private static readonly Dictionary<string, string> MonitorEnvironmentVariables = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.MonitorEnvironment) }
        };

        private static readonly string[] MonitorUrls = new[] { nameof(ConfigurationLevel.HostBuilderSettingsUrl) };

        private static readonly Dictionary<string, string> SharedSettingsContent = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.SharedSettings) }
        };

        private static readonly Dictionary<string, string> UserSettingsContent = new(StringComparer.Ordinal)
        {
            { WebHostDefaults.ServerUrlsKey, nameof(ConfigurationLevel.UserSettings) }
        };

        // This needs to be updated and kept in order for any future configuration sections
        private static readonly List<string> OrderedConfigurationKeys = new()
        {
            "urls",
            "Kestrel",
            "GlobalCounter",
            "CollectionRules",
            "CorsConfiguration",
            "DiagnosticPort",
            "Metrics",
            "Storage",
            "DefaultProcess",
            "Logging",
            "Authentication",
            "Egress"
        };

        private readonly ITestOutputHelper _outputHelper;

        public ConfigurationTests(ITestOutputHelper outputHelper)
        {
            _outputHelper = outputHelper;
        }

        /// <summary>
        /// Tests that when specifying a configuration value at the given configuration level
        /// that the value overrides any other value provided by another configuration source
        /// with lower precedence.
        /// </summary>
        [Theory]
        [InlineData(ConfigurationLevel.None)]
        [InlineData(ConfigurationLevel.HostBuilderSettingsUrl)]
        [InlineData(ConfigurationLevel.DotnetEnvironment)]
        [InlineData(ConfigurationLevel.AspnetEnvironment)]
        [InlineData(ConfigurationLevel.AppSettings)]
        [InlineData(ConfigurationLevel.UserSettings)]
        [InlineData(ConfigurationLevel.SharedSettings)]
        [InlineData(ConfigurationLevel.SharedKeyPerFile)]
        [InlineData(ConfigurationLevel.MonitorEnvironment)]
        public void ConfigurationOrderingTest(ConfigurationLevel level)
        {
            using TemporaryDirectory contentRootDirectory = new(_outputHelper);
            using TemporaryDirectory sharedConfigDir = new(_outputHelper);
            using TemporaryDirectory userConfigDir = new(_outputHelper);

            // Set up the initial settings used to create the host builder.
            HostBuilderSettings settings = new()
            {
                Authentication = HostBuilderHelper.CreateAuthConfiguration(noAuth: false, tempApiKey: false),
                ContentRootDirectory = contentRootDirectory.FullName,
                SharedConfigDirectory = sharedConfigDir.FullName,
                UserConfigDirectory = userConfigDir.FullName
            };
            if (level >= ConfigurationLevel.HostBuilderSettingsUrl)
            {
                settings.Urls = MonitorUrls;
            }

            // Write all of the test files
            if (level >= ConfigurationLevel.AppSettings)
            {
                // This is the appsettings.json file that is normally next to the entrypoint assembly.
                // The location of the appsettings.json is determined by the content root in configuration.
                string appSettingsContent = JsonSerializer.Serialize(AppSettingsContent);
                File.WriteAllText(Path.Combine(contentRootDirectory.FullName, "appsettings.json"), appSettingsContent);
            }
            if (level >= ConfigurationLevel.UserSettings)
            {
                // This is the settings.json file in the user profile directory.
                string userSettingsContent = JsonSerializer.Serialize(UserSettingsContent);
                File.WriteAllText(Path.Combine(userConfigDir.FullName, "settings.json"), userSettingsContent);
            }
            if (level >= ConfigurationLevel.SharedSettings)
            {
                // This is the settings.json file in the shared configuration directory that is visible
                // to all users on the machine e.g. /etc/dotnet-monitor on Unix systems.
                string sharedSettingsContent = JsonSerializer.Serialize(SharedSettingsContent);
                File.WriteAllText(Path.Combine(sharedConfigDir.FullName, "settings.json"), sharedSettingsContent);
            }
            if (level >= ConfigurationLevel.SharedKeyPerFile)
            {
                // This is a key-per-file file in the shared configuration directory. This configuration
                // is typically used when mounting secrets from a Docker volume.
                File.WriteAllText(Path.Combine(sharedConfigDir.FullName, WebHostDefaults.ServerUrlsKey), nameof(ConfigurationLevel.SharedKeyPerFile));
            }

            // Create the initial host builder.
            IHostBuilder builder = HostBuilderHelper.CreateHostBuilder(settings);

            // Override the environment configurations to use predefined values so that the test host
            // doesn't inadvertently provide unexpected values. Passing null replaces with an empty
            // in-memory collection source.
            builder.ReplaceAspnetEnvironment(level >= ConfigurationLevel.AspnetEnvironment ? AspnetEnvironmentVariables : null);
            builder.ReplaceDotnetEnvironment(level >= ConfigurationLevel.DotnetEnvironment ? DotnetEnvironmentVariables : null);
            builder.ReplaceMonitorEnvironment(level >= ConfigurationLevel.MonitorEnvironment ? MonitorEnvironmentVariables : null);

            // Build the host and get the Urls property from configuration.
            IHost host = builder.Build();
            IConfiguration rootConfiguration = host.Services.GetRequiredService<IConfiguration>();
            string configuredUrls = rootConfiguration[WebHostDefaults.ServerUrlsKey];

            // Test that the value of the Urls property is the same as it was set
            // for the level of configuration of the test.
            if (level == ConfigurationLevel.None)
            {
                Assert.Null(configuredUrls);
            }
            else
            {
                Assert.Equal(Enum.GetName(level), configuredUrls);
            }
        }

        /// <summary>
        /// Instead of having to explicitly define every expected value, this reuses the individual categories to ensure they
        /// assemble properly when combined.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FullConfigurationTest(bool redact)
        {
            using TemporaryDirectory contentRootDirectory = new(_outputHelper);
            using TemporaryDirectory sharedConfigDir = new(_outputHelper);
            using TemporaryDirectory userConfigDir = new(_outputHelper);

            // Set up the initial settings used to create the host builder.
            HostBuilderSettings settings = new()
            {
                Authentication = HostBuilderHelper.CreateAuthConfiguration(noAuth: false, tempApiKey: false),
                ContentRootDirectory = contentRootDirectory.FullName,
                SharedConfigDirectory = sharedConfigDir.FullName,
                UserConfigDirectory = userConfigDir.FullName
            };

            // This is the settings.json file in the user profile directory.
            File.WriteAllText(Path.Combine(userConfigDir.FullName, "settings.json"), ConstructUserSettingsJson());

            // Create the initial host builder.
            IHostBuilder builder = HostBuilderHelper.CreateHostBuilder(settings);

            // Override the environment configurations to use predefined values so that the test host
            // doesn't inadvertently provide unexpected values. Passing null replaces with an empty
            // in-memory collection source.
            builder.ReplaceAspnetEnvironment();
            builder.ReplaceDotnetEnvironment();
            builder.ReplaceMonitorEnvironment();

            // Build the host and get the Urls property from configuration.
            IHost host = builder.Build();
            IConfiguration rootConfiguration = host.Services.GetRequiredService<IConfiguration>();

            Stream stream = new MemoryStream();

            using ConfigurationJsonWriter jsonWriter = new ConfigurationJsonWriter(stream);
            jsonWriter.Write(rootConfiguration, full: !redact, skipNotPresent: false);
            jsonWriter.Dispose();

            stream.Position = 0;

            using (var streamReader = new StreamReader(stream))
            {
                string configString = streamReader.ReadToEnd();

                _outputHelper.WriteLine(configString);

                Assert.Equal(CleanWhitespace(configString), CleanWhitespace(ConstructExpectedOutput(redact)));
            }

            static string CleanWhitespace(string rawText)
            {
                return string.Concat(rawText.Where(c => !char.IsWhiteSpace(c)));
            }
        }

        private string ConstructUserSettingsJson()
        {
            string[] fileNames = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "SampleConfigurations"));

            IDictionary<string, JsonElement> combinedFiles = new Dictionary<string, JsonElement>();

            foreach (var fileName in fileNames)
            {
                IDictionary<string, JsonElement> deserializedFile = JsonSerializer.Deserialize<IDictionary<string, JsonElement>>(File.ReadAllText(fileName));

                foreach ((string key, JsonElement element) in deserializedFile)
                {
                    combinedFiles.Add(key, element);
                }
            }

            string generatedUserSettings = JsonSerializer.Serialize(combinedFiles);

            return generatedUserSettings;
        }

        private string ConstructExpectedOutput(bool redact)
        {
            Dictionary<string, string> categoryMapping = GetConfigurationFileNames(redact);

            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);

            writer.WriteStartObject();

            foreach (var key in OrderedConfigurationKeys)
            {
                writer.WritePropertyName(key);

                if (categoryMapping.TryGetValue(key, out string fileName))
                {
                    string expectedPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ExpectedConfigurations", fileName);

                    writer.WriteRawValue(File.ReadAllText(expectedPath));
                }
                else
                {
                    writer.WriteStringValue(Strings.Placeholder_NotPresent);
                }
            }

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private Dictionary<string, string> GetConfigurationFileNames(bool redact)
        {
            return new Dictionary<string, string>()
            {
                { "GlobalCounter", "GlobalCounter.json" },
                { "Metrics", "Metrics.json" },
                { "Egress", redact ? "EgressRedacted.json" : "EgressFull.json" },
                { "Storage", "Storage.json" },
                { "urls", "URLs.json" },
                { "Logging", "Logging.json" },
                { "DefaultProcess", "DefaultProcess.json" },
                { "DiagnosticPort", "DiagnosticPort.json" },
                { "CollectionRules", "CollectionRules.json" },
                { "Authentication", redact ? "AuthenticationRedacted.json" : "AuthenticationFull.json" }
            };
        }

        /// This is the order of configuration sources where a name with a lower
        /// enum value has a lower precedence in configuration.
        public enum ConfigurationLevel
        {
            None,
            HostBuilderSettingsUrl,
            DotnetEnvironment,
            AspnetEnvironment,
            AppSettings,
            UserSettings,
            SharedSettings,
            SharedKeyPerFile,
            MonitorEnvironment
        }
    }
}