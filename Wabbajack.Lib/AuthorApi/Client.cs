﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib.ModListRegistry;

namespace Wabbajack.Lib.AuthorApi
{
    public class Client
    {
        public static async Task<Client> Create(string? apiKey = null)
        {
            var client = await GetAuthorizedClient(apiKey);
            return new Client(client); 
        }

        private Client(Wabbajack.Lib.Http.Client client)
        {
            _client = client;
        }
        
        public static async Task<Wabbajack.Lib.Http.Client> GetAuthorizedClient(string? apiKey = null)
        {
            var client = new Wabbajack.Lib.Http.Client();
            client.Headers.Add(("X-API-KEY", await GetAPIKey(apiKey)));
            return client;
        }

        public static string? ApiKeyOverride = null;
        private Wabbajack.Lib.Http.Client _client;

        public static async ValueTask<string> GetAPIKey(string? apiKey = null)
        {
            return apiKey ?? (await Consts.LocalAppDataPath.Combine(Consts.AuthorAPIKeyFile).ReadAllTextAsync()).Trim();
        }


        public static async Task<CDNFileDefinition> GenerateFileDefinition(WorkQueue queue, AbsolutePath path, Action<string, Percent> progressFn)
        {
            IEnumerable<CDNFilePartDefinition> Blocks(AbsolutePath path)
            {
                var size = path.Size;
                for (long block = 0; block * Consts.UploadedFileBlockSize < size; block ++)
                    yield return new CDNFilePartDefinition
                    {
                        Index = block,
                        Size = Math.Min(Consts.UploadedFileBlockSize, size - block * Consts.UploadedFileBlockSize),
                        Offset = block * Consts.UploadedFileBlockSize
                    };
            }
            
            var parts = Blocks(path).ToArray();
            var definition = new CDNFileDefinition
            {
                OriginalFileName = path.FileName, 
                Size = path.Size, 
                Hash = await path.FileHashCachedAsync() ?? Hash.Empty,
                Parts = await parts.PMap(queue, async part =>
                {
                    progressFn("Hashing file parts", Percent.FactoryPutInRange(part.Index, parts.Length));
                    var buffer = new byte[part.Size];
                    await using (var fs = await path.OpenShared())
                    {
                        fs.Position = part.Offset;
                        await fs.ReadAsync(buffer);
                    }
                    part.Hash = buffer.xxHash();
                    return part;
                })
            };

            return definition;
        }

        public async Task UpdateModListInformation(string machineUrl, DownloadMetadata metadata)
        {
            await CircuitBreaker.WithAutoRetryAllAsync(async () =>
            {
                Utils.Log($"Updating modlist information for {machineUrl} - {metadata.Version}");
                using var result = await _client.PostAsync($"{Consts.WabbajackBuildServerUri}author_controls/{machineUrl}/download_metadata",
                    new StringContent(metadata.ToJson()));
            });
        }

        public async Task<IReadOnlyList<(string MachineURL, Version Version)>> GetMyModlists()
        {
            var myLists = await _client.GetJsonAsync<string[]>($"{Consts.WabbajackBuildServerUri}author_controls/lists");
            List<(string MachineURL, Version Version)> lists = new();
            var client = await GitHub.Client.Get();
            foreach (var file in Enum.GetValues<GitHub.Client.List>())
            {
                foreach (var lst in (await client.GetData(file)).Lists)
                {
                    if (myLists.Contains(lst.Links.MachineURL))
                    {
                        lists.Add((lst.Links.MachineURL, lst.Version ?? new Version()));
                    }
                }
            }
            return lists;
        }

        public async Task<Uri> UploadFile(WorkQueue queue, AbsolutePath path, Action<string, Percent> progressFn)
        {
            var definition = await GenerateFileDefinition(queue, path, progressFn);

            await CircuitBreaker.WithAutoRetryAllAsync(async () =>
            {
                using var result = await _client.PutAsync($"{Consts.WabbajackBuildServerUri}authored_files/create",
                    new StringContent(definition.ToJson()));
                progressFn("Starting upload", Percent.Zero);
                definition.ServerAssignedUniqueId = await result.Content.ReadAsStringAsync();
            });

            var results = await definition.Parts.PMap(queue, async part =>
            {
                progressFn("Uploading Part", Percent.FactoryPutInRange(part.Index, definition.Parts.Length));
                var buffer = new byte[part.Size];
                await using (var fs = await path.OpenShared())
                {
                    fs.Position = part.Offset;
                    await fs.ReadAsync(buffer);
                }
                
                return await CircuitBreaker.WithAutoRetryAllAsync(async () =>
                {
                    using var putResult = await _client.PutAsync(
                        $"{Consts.WabbajackBuildServerUri}authored_files/{definition.ServerAssignedUniqueId}/part/{part.Index}",
                        new ByteArrayContent(buffer));
                    var hash = Hash.FromBase64(await putResult.Content.ReadAsStringAsync());
                    if (hash != part.Hash)
                        throw new InvalidDataException("Hashes don't match");
                    return hash;
                });

            });
            
            progressFn("Finalizing upload", Percent.Zero);
            return await CircuitBreaker.WithAutoRetryAllAsync(async () =>
            {
                using var result = await _client.PutAsync(
                    $"{Consts.WabbajackBuildServerUri}authored_files/{definition.ServerAssignedUniqueId}/finish",
                    new StringContent(definition.ToJson()));
                progressFn("Finished", Percent.One);
                return new Uri(await result.Content.ReadAsStringAsync());
            });
        }
    }
}
