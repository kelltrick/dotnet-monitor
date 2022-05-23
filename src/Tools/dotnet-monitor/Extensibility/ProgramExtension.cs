﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Tools.Monitor.Egress;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Monitor.Extensibility
{
    internal class ProgramExtension : IExtension, IEgressExtension
    {
        private readonly string _extensionName;
        private readonly string _targetFolder;
        private readonly string _declarationPath;
        private readonly IFileProvider _fileSystem;
        private readonly ILogger<ProgramExtension> _logger;
        private Lazy<ExtensionDeclaration> _extensionDeclaration;

        public ProgramExtension(string extensionName, string targetFolder, IFileProvider fileSystem, string declarationPath, ILogger<ProgramExtension> logger)
        {
            _extensionName = extensionName;
            _targetFolder = targetFolder;
            _fileSystem = fileSystem;
            _declarationPath = declarationPath;
            _logger = logger;
            _extensionDeclaration = new Lazy<ExtensionDeclaration>(() => { return GetExtensionDeclaration(); }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <inheritdoc/>
        public string DisplayName => Path.GetDirectoryName(_declarationPath);

        private ExtensionDeclaration Declaration
        {
            get
            {
                return _extensionDeclaration.Value;
            }
        }

        /// <inheritdoc/>
        bool IExtension.TryGetTypedExtension<TExtensionType>(out TExtensionType extension)
        {
            // Is Egress type requested and extension supports Egress
            if (typeof(TExtensionType) == typeof(IEgressExtension) && Declaration.SupportedExtensionTypes.Contains(ExtensionTypes.Egress))
            {
                extension = (TExtensionType) (IExtension) this;
                return true;
            }

            extension = null;
            return false;
        }

        /// <inheritdoc/>
        public async Task<EgressArtifactResult> EgressArtifact(ExtensionEgressPayload configPayload, Func<Stream, CancellationToken, Task> getStreamAction, CancellationToken token)
        {
            // This _should_ only be used in this method, it can get moved to a constants class if that changes
            const string CommandArgProviderName = "--Provider-Name";
            // This is really weird, yes, but this is one of 2 overloads for [Stream].WriteAsync(...) that supports a CancellationToken, so we use a ReadOnlyMemory<char> instead of a string.
            ReadOnlyMemory<char> NewLine = new ReadOnlyMemory<char>("\r\n".ToCharArray());

            string programRelPath = Path.Combine(Path.GetDirectoryName(_declarationPath), Declaration.Program);
            IFileInfo progInfo = _fileSystem.GetFileInfo(programRelPath);
            if (!progInfo.Exists || progInfo.IsDirectory || progInfo.PhysicalPath == null)
            {
                _logger.ExtensionProgramMissing(_extensionName, Path.Combine(_targetFolder, _declarationPath), Declaration.Program);
                throw new ExtensionNotFoundException(_extensionName);
            }

            /* [TODOs]
             * 1. [Done] Add a new service to dynamically find these extension(s)
             * 2. [Done] Remove all raw logging statements from this method and refactor into LoggingExtensions
             * 3. Stream StdOut and StdErr async in the process so their streams don't need to end before we can return
             * 4. [Done] Refactor WaitForExit to do an async wait
             * 5. Add well-factored protocol for returning information from an extension
             */
            ProcessStartInfo pStart = new ProcessStartInfo()
            {
                FileName = progInfo.PhysicalPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            pStart.ArgumentList.Add(ExtensionTypes.Egress);
            pStart.ArgumentList.Add(CommandArgProviderName);
            pStart.ArgumentList.Add(configPayload.ProviderName);

            Process p = new Process()
            {
                StartInfo = pStart,
            };

            using OutputParser<EgressArtifactResult> parser = new(p, _logger);

            _logger.ExtensionStarting(pStart.FileName, pStart.Arguments);
            if (!p.Start())
            {
                throw new ExtensionLaunchFailure(_extensionName);
            }

            await JsonSerializer.SerializeAsync<ExtensionEgressPayload>(p.StandardInput.BaseStream, configPayload, options: null, token);
            await p.StandardInput.WriteAsync(NewLine, token);
            await p.StandardInput.BaseStream.FlushAsync(token);
            _logger.ExtensionConfigured(pStart.FileName, p.Id);

            await getStreamAction(p.StandardInput.BaseStream, token);
            await p.StandardInput.BaseStream.FlushAsync(token);
            p.StandardInput.Close();
            _logger.ExtensionEgressPayloadCompleted(p.Id);

            await p.WaitForExitAsync(token);
            _logger.ExtensionExited(p.Id, p.ExitCode);

            return null;
        }

        private ExtensionDeclaration GetExtensionDeclaration()
        {
            IFileInfo declFile = _fileSystem.GetFileInfo(_declarationPath);
            if (!declFile.Exists || declFile.IsDirectory)
            {
                LogBrokenDeclaration(null);
                throw new ExtensionNotFoundException(_extensionName);
            }

            try
            {
                using (Stream declStream = declFile.CreateReadStream())
                {
                    ExtensionDeclaration declResult = JsonSerializer.Deserialize<ExtensionDeclaration>(declStream);
                    return declResult;
                }
            }
            catch (Exception ex) when (LogBrokenDeclaration(ex))
            {
                // This will never get hit, LogBrokenDeclaration will never filter this exception
                // Do the logging in the filter so that the exception stack remains complete
                throw;
            }
        }

        private bool LogBrokenDeclaration(Exception ex)
        {
            _logger.ExtensionDeclarationFileBroken(_extensionName, Path.Combine(_targetFolder, _declarationPath), ex);
            return false;
        }
    }
}
