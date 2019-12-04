﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using DynamicData;
using Microsoft.WindowsAPICodePack.Shell;
using Newtonsoft.Json;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Lib.NexusApi;
using File = Alphaleonis.Win32.Filesystem.File;

namespace Wabbajack.Lib
{
    public class VortexCompiler : ACompiler
    {
        /*  vortex creates a vortex.deployment.json file that contains information
            about all deployed files, parsing that file, we can get a list of all 'active'
            archives so we don't force the user to install all archives found in the downloads folder.
            Similar to how IgnoreDisabledMods for MO2 works
        */
        public VortexDeployment VortexDeployment;
        public List<string> ActiveArchives;

        public Game Game { get; }
        public string GameName { get; }

        public string VortexFolder { get; set; }
        public string StagingFolder { get; set; }
        public string DownloadsFolder { get; set; }

        public bool IgnoreMissingFiles { get; set; }

        public override ModManager ModManager => ModManager.Vortex;
        public override string GamePath { get; }
        public override string ModListOutputFolder { get; }
        public override string ModListOutputFile { get; }

        public const string StagingMarkerName = "__vortex_staging_folder";
        public const string DownloadMarkerName = "__vortex_downloads_folder";

        private bool _isSteamGame;
        private SteamGame _steamGame;
        private bool _hasSteamWorkshopItems;

        public VortexCompiler(Game game, string gamePath, string vortexFolder, string downloadsFolder, string stagingFolder, string outputFile)
        {
            Game = game;

            GamePath = gamePath;
            GameName = GameRegistry.Games[game].NexusName;
            VortexFolder = vortexFolder;
            DownloadsFolder = downloadsFolder;
            StagingFolder = stagingFolder;
            ModListOutputFolder = "output_folder";
            ModListOutputFile = outputFile;

            // there can be max one game after filtering
            SteamHandler.Instance.Games.Where(g => g.Game != null && g.Game == game).Do(g =>
            {
                _isSteamGame = true;
                _steamGame = g;
                SteamHandler.Instance.LoadWorkshopItems(_steamGame);
                _hasSteamWorkshopItems = _steamGame.WorkshopItems.Count > 0;
            });

            ActiveArchives = new List<string>();
        }
        
        protected override async Task<bool> _Begin(CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested) return false;

            ConfigureProcessor(10);
            if (string.IsNullOrEmpty(ModListName))
                ModListName = $"Vortex ModList for {Game.ToString()}";

            Info($"Starting Vortex compilation for {GameName} at {GamePath} with staging folder at {StagingFolder} and downloads folder at  {DownloadsFolder}.");

            if (cancel.IsCancellationRequested) return false;
            ParseDeploymentFile();

            if (cancel.IsCancellationRequested) return false;
            Info("Starting pre-compilation steps");
            CreateMetaFiles();

            if (cancel.IsCancellationRequested) return false;
            Info($"Indexing {StagingFolder}");
            await VFS.AddRoot(StagingFolder);

            Info($"Indexing {GamePath}");
            await VFS.AddRoot(GamePath);

            Info($"Indexing {DownloadsFolder}");
            await VFS.AddRoot(DownloadsFolder);

            if (cancel.IsCancellationRequested) return false;
            await AddExternalFolder();

            if (cancel.IsCancellationRequested) return false;
            Info("Cleaning output folder");
            if (Directory.Exists(ModListOutputFolder)) Utils.DeleteDirectory(ModListOutputFolder);
            Directory.CreateDirectory(ModListOutputFolder);
            
            var vortexStagingFiles = Directory.EnumerateFiles(StagingFolder, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists() && p != StagingMarkerName)
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    {Path = p.RelativeTo(StagingFolder)});
            
            var vortexDownloads = Directory.EnumerateFiles(DownloadsFolder, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    {Path = p.RelativeTo(DownloadsFolder)});

            var gameFiles = Directory.EnumerateFiles(GamePath, "*", SearchOption.AllDirectories)
                .Where(p => p.FileExists())
                .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p])
                    { Path = Path.Combine(Consts.GameFolderFilesDir, p.RelativeTo(GamePath)) });

            Info("Indexing Archives");
            IndexedArchives = Directory.EnumerateFiles(DownloadsFolder)
                .Where(f => File.Exists(f + ".meta"))
                .Select(f => new IndexedArchive
                {
                    File = VFS.Index.ByRootPath[f],
                    Name = Path.GetFileName(f),
                    IniData = (f + ".meta").LoadIniFile(),
                    Meta = File.ReadAllText(f + ".meta")
                })
                .ToList();

            Info("Indexing Files");
            IndexedFiles = IndexedArchives.SelectMany(f => f.File.ThisAndAllChildren)
                .OrderBy(f => f.NestingFactor)
                .GroupBy(f => f.Hash)
                .ToDictionary(f => f.Key, f => f.AsEnumerable());

            Info("Searching for mod files");
            AllFiles = vortexStagingFiles.Concat(vortexDownloads)
                .Concat(gameFiles)
                .DistinctBy(f => f.Path)
                .ToList();

            Info($"Found {AllFiles.Count} files to build into mod list");

            if (cancel.IsCancellationRequested) return false;
            Info("Verifying destinations");
            var duplicates = AllFiles.GroupBy(f => f.Path)
                .Where(fs => fs.Count() > 1)
                .Select(fs =>
                {
                    Utils.Log($"Duplicate files installed to {fs.Key} from : {string.Join(", ", fs.Select(f => f.AbsolutePath))}");
                    return fs;
                }).ToList();

            if (duplicates.Count > 0)
            {
                Error($"Found {duplicates.Count} duplicates, exiting");
            }

            for (var i = 0; i < AllFiles.Count; i++)
            {
                var f = AllFiles[i];
                if (!f.Path.StartsWith(Consts.GameFolderFilesDir) || !IndexedFiles.ContainsKey(f.Hash))
                    continue;

                if (!IndexedFiles.TryGetValue(f.Hash, out var value))
                    continue;

                var element = value.ElementAt(0);

                if (!f.Path.Contains(element.Name))
                    continue;

                IndexedArchive targetArchive = null;
                IndexedArchives.Where(a => a.File.Children.Contains(element)).Do(a => targetArchive = a);

                if (targetArchive == null)
                    continue;
                
                if(targetArchive.IniData?.General?.tag == null || targetArchive.IniData?.General?.tag != Consts.WABBAJACK_VORTEX_MANUAL)
                    continue;

                #if DEBUG
                Utils.Log($"Double hash for: {f.AbsolutePath}");
                #endif

                var replace = f;
                replace.Path = Path.Combine("Manual Game Files", element.FullPath.Substring(DownloadsFolder.Length + 1).Replace('|', '\\'));
                AllFiles.RemoveAt(i);
                AllFiles.Insert(i, replace);
                //AllFiles.Replace(f, replace);
            }

            var stack = MakeStack();

            Info("Running Compilation Stack");
            var results = await AllFiles.PMap(Queue, f => RunStack(stack.Where(s => s != null), f));

            IEnumerable<NoMatch> noMatch = results.OfType<NoMatch>().ToList();
            Info($"No match for {noMatch.Count()} files");
            foreach (var file in noMatch)
                Info($"     {file.To}");
            if (noMatch.Any())
            {
                if (IgnoreMissingFiles)
                {
                    Info("Continuing even though files were missing at the request of the user.");
                }
                else
                {
                    Info("Exiting due to no way to compile these files");
                    return false;
                }
            }

            InstallDirectives = results.Where(i => !(i is IgnoredDirectly)).ToList();

            // TODO: nexus stuff
            /*Info("Getting Nexus api_key, please click authorize if a browser window appears");
            if (IndexedArchives.Any(a => a.IniData?.General?.gameName != null))
            {
                var nexusClient = new NexusApiClient();
                if (!nexusClient.IsPremium) Error($"User {nexusClient.Username} is not a premium Nexus user, so we cannot access the necessary API calls, cannot continue");

            }
            */

            if (cancel.IsCancellationRequested) return false;
            await GatherArchives();

            ModList = new ModList
            {
                Name = ModListName ?? "",
                Author = ModListAuthor ?? "",
                Description = ModListDescription ?? "",
                Readme = ModListReadme ?? "",
                Image = ModListImage ?? "",
                Website = ModListWebsite ?? "",
                Archives = SelectedArchives.ToList(),
                ModManager = ModManager.Vortex,
                Directives = InstallDirectives,
                GameType = Game
            };
            
            GenerateReport();
            ExportModList();

            Info("Done Building ModList");

            ShowReport();
            return true;
        }

        private void ParseDeploymentFile()
        {
            Info("Searching for vortex.deployment.json...");

            var deploymentFile = "";
            Directory.EnumerateFiles(GamePath, "vortex.deployment.json", SearchOption.AllDirectories)
                .Where(File.Exists)
                .Do(f => deploymentFile = f);
            var currentGame = GameRegistry.Games[Game];
            if (currentGame.AdditionalFolders != null && currentGame.AdditionalFolders.Count != 0)
                currentGame.AdditionalFolders.Do(f => Directory.EnumerateFiles(f, "vortex.deployment.json", SearchOption.AllDirectories)
                    .Where(File.Exists)
                    .Do(d => deploymentFile = d));

            if (string.IsNullOrEmpty(deploymentFile))
            {
                Info("vortex.deployment.json not found!");
                return;
            }
            Info("vortex.deployment.json found at "+deploymentFile);

            Info("Parsing vortex.deployment.json...");
            try
            {
                VortexDeployment = deploymentFile.FromJSON<VortexDeployment>();
            }
            catch (JsonSerializationException e)
            {
                Utils.Error(e, "Failed to parse vortex.deployment.json!");
            }

            VortexDeployment.files.Do(f =>
            {
                var archive = f.source;
                if (ActiveArchives.Contains(archive))
                    return;

                Utils.Log($"Adding Archive {archive} to ActiveArchives");
                ActiveArchives.Add(archive);
            });
        }

        /// <summary>
        /// Some have mods outside their game folder located
        /// </summary>
        private async Task AddExternalFolder()
        {
            var currentGame = GameRegistry.Games[Game];
            if (currentGame.AdditionalFolders == null || currentGame.AdditionalFolders.Count == 0) return;
            foreach (var f in currentGame.AdditionalFolders)
            {
                var path = f.Replace("%documents%", KnownFolders.Documents.Path);
                if (!Directory.Exists(path)) return;
                Info($"Indexing {path}");
                await VFS.AddRoot(path);
            }
        }

        private void CreateMetaFiles()
        {
            Utils.Log("Getting Nexus api_key, please click authorize if a browser window appears");
            var nexusClient = new NexusApiClient();

            Directory.EnumerateFiles(DownloadsFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(File.Exists)
                .Do(f =>
                {
                    if (Path.GetExtension(f) != ".meta" && !File.Exists($"{f}.meta") && ActiveArchives.Contains(Path.GetFileNameWithoutExtension(f)))
                    {
                        Utils.Log($"Trying to create meta file for {Path.GetFileName(f)}");
                        var metaString = "[General]\n" +
                                         "repository=Nexus\n" +
                                         "installed=true\n" +
                                         "uninstalled=false\n" +
                                         "paused=false\n" +
                                         "removed=false\n" +
                                         $"gameName={GameName}\n";
                        string hash;
                        using(var md5 = MD5.Create())
                        using (var stream = File.OpenRead(f))
                        {
                            Utils.Log($"Calculating hash for {Path.GetFileName(f)}");
                            var cH = md5.ComputeHash(stream);
                            hash = BitConverter.ToString(cH).Replace("-", "").ToLowerInvariant();
                            Utils.Log($"Hash is {hash}");
                        }

                        var md5Response = nexusClient.GetModInfoFromMD5(Game, hash);
                        if (md5Response.Count >= 1)
                        {
                            var modInfo = md5Response[0].mod;
                            metaString += $"modID={modInfo.mod_id}\n" +
                                          $"modName={modInfo.name}\nfileID={md5Response[0].file_details.file_id}";
                            File.WriteAllText(f+".meta",metaString, Encoding.UTF8);
                        }
                        else
                        {
                            Error("Error while getting information from NexusMods via MD5 hash!");
                        }
                    }
                    else
                    {
                        if (Path.GetExtension(f) != ".meta" ||
                            ActiveArchives.Contains(Path.GetFileNameWithoutExtension(f)))
                            return;

                        Utils.Log($"File {f} is not in ActiveArchives");
                        var lines = File.ReadAllLines(f);
                        if (lines.Length == 0 || !lines.Any(line => line.Contains("directURL=")))
                        {
                            if (lines.Length == 0)
                                return;

                            lines.Do(line =>
                            {
                                var tag = "";
                                if (line.Contains("tag="))
                                    tag = line.Substring("tag=".Length);

                                if (tag != Consts.WABBAJACK_VORTEX_MANUAL)
                                    return;

                                Utils.Log($"File {f} contains the {Consts.WABBAJACK_VORTEX_MANUAL} tag, adding to ActiveArchives");
                                ActiveArchives.Add(Path.GetFileNameWithoutExtension(f));
                            });
                        }
                        else
                        {
                            Utils.Log($"File {f} appears to not come from the Nexus, adding to ActiveArchives");
                            ActiveArchives.Add(Path.GetFileNameWithoutExtension(f));
                        }
                    }
                });

            Utils.Log($"Checking for Steam Workshop Items...");
            if (!_isSteamGame || _steamGame == null || _steamGame.WorkshopItems.Count <= 0)
                return;

            _steamGame.WorkshopItems.Do(item =>
            {
                var filePath = Path.Combine(DownloadsFolder, $"steamWorkshopItem_{item.ItemID}.meta");
                if (File.Exists(filePath))
                {
                    Utils.Log($"File {filePath} already exists, skipping...");
                    return;
                }

                Utils.Log($"Creating meta file for {item.ItemID}");
                var metaString = "[General]\n" +
                                 "repository=Steam\n" +
                                 "installed=true\n" +
                                 $"gameName={GameName}\n" +
                                 $"steamID={_steamGame.AppId}\n" +
                                 $"itemID={item.ItemID}\n" +
                                 $"itemSize={item.Size}\n";
                try
                {
                    File.WriteAllText(filePath, metaString);
                }
                catch (Exception e)
                {
                    Utils.Error(e, $"Exception while writing to disk at {filePath}");
                }
            });
        }

        public override IEnumerable<ICompilationStep> GetStack()
        {
            var s = Consts.TestMode ? DownloadsFolder : VortexFolder;
            var userConfig = Path.Combine(s, "compilation_stack.yml");
            if (File.Exists(userConfig))
                return Serialization.Deserialize(File.ReadAllText(userConfig), this);

            var stack = MakeStack();

            File.WriteAllText(Path.Combine(s, "_current_compilation_stack.yml"),
                Serialization.Serialize(stack));

            return stack;
        }

        public override IEnumerable<ICompilationStep> MakeStack()
        {
            Utils.Log("Generating compilation stack");
            return new List<ICompilationStep>
            {
                new IncludePropertyFiles(this),
                new IncludeSteamWorkshopItems(this, _steamGame),
                _hasSteamWorkshopItems ? new IncludeRegex(this, "^steamWorkshopItem_\\d*\\.meta$") : null,
                new IgnoreDisabledVortexMods(this),
                new IncludeVortexDeployment(this),
                new IgnoreVortex(this),
                new IgnoreRegex(this, $"^*{StagingMarkerName}$"),

                Game == Game.DarkestDungeon ? new IncludeRegex(this, "project\\.xml$") : null,

                new IgnoreStartsWith(this, StagingFolder),
                new IgnoreEndsWith(this, StagingFolder),

                new IgnoreGameFiles(this),

                new DirectMatch(this),

                new IgnoreGameFiles(this),

                new IgnoreWabbajackInstallCruft(this),

                new DropAll(this)
            };
        }

        public static string TypicalVortexFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex");
        }

        public static string RetrieveDownloadLocation(Game game, string vortexFolderPath = null)
        {
            vortexFolderPath = vortexFolderPath ?? TypicalVortexFolder();
            return Path.Combine(vortexFolderPath, "downloads", GameRegistry.Games[game].NexusName);
        }

        public static string RetrieveStagingLocation(Game game, string vortexFolderPath = null)
        {
            vortexFolderPath = vortexFolderPath ?? TypicalVortexFolder();
            var gameName = GameRegistry.Games[game].NexusName;
            return Path.Combine(vortexFolderPath, gameName, "mods");
        }

        public static IErrorResponse IsValidBaseDownloadsFolder(string path)
        {
            if (!Directory.Exists(path)) return ErrorResponse.Fail($"Path does not exist: {path}");
            if (Directory.EnumerateFiles(path, DownloadMarkerName, SearchOption.TopDirectoryOnly).Any()) return ErrorResponse.Success;
            return ErrorResponse.Fail($"Folder must contain {DownloadMarkerName} file");
        }

        public static IErrorResponse IsValidDownloadsFolder(string path)
        {
            return IsValidBaseDownloadsFolder(Path.GetDirectoryName(path));
        }

        public static IErrorResponse IsValidBaseStagingFolder(string path)
        {
            if (!Directory.Exists(path)) return ErrorResponse.Fail($"Path does not exist: {path}");
            if (Directory.EnumerateFiles(path, StagingMarkerName, SearchOption.TopDirectoryOnly).Any()) return ErrorResponse.Success;
            return ErrorResponse.Fail($"Folder must contain {StagingMarkerName} file");
        }

        public static IErrorResponse IsValidStagingFolder(string path)
        {
            return IsValidBaseStagingFolder(Path.GetDirectoryName(path));
        }

        public static bool IsActiveVortexGame(Game g)
        {
            return GameRegistry.Games[g].SupportedModManager == ModManager.Vortex && !GameRegistry.Games[g].Disabled;
        }
    }

    public class VortexDeployment
    {
        public string instance;
        public int version;
        public string deploymentMethod;
        public List<VortexFile> files;
    }

    public class VortexFile
    {
        public string relPath;
        public string source;
        public string target;
    }
}
