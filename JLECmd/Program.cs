﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using ExtensionBlocks;
using Fclp;
using Fclp.Internals.Extensions;
using JumpList.Automatic;
using JumpList.Custom;
using Lnk;
using Lnk.ExtraData;
using Lnk.ShellItems;
using Microsoft.Win32;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServiceStack;
using ServiceStack.Text;
using CsvWriter = CsvHelper.CsvWriter;

namespace JLECmd
{
    internal class Program
    {
        private const string SSLicenseFile = @"D:\SSLic.txt";
        private static Logger _logger;

        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;

        private static List<string> _failedFiles;

        private static readonly Dictionary<string, string> _macList = new Dictionary<string, string>();

        private static List<AutomaticDestination> _processedAutoFiles;
        private static List<CustomDestination> _processedCustomFiles;

        private static bool CheckForDotnet46()
        {
            using (
                var ndpKey =
                    RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                        .OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\\"))
            {
                var releaseKey = Convert.ToInt32(ndpKey?.GetValue("Release"));

                return releaseKey >= 393295;
            }
        }


        private static void Main(string[] args)
        {
            Licensing.RegisterLicenseFromFileIfExists(SSLicenseFile);

            //LoadMACs();

            SetupNLog();

            _logger = LogManager.GetCurrentClassLogger();

            if (!CheckForDotnet46())
            {
                _logger.Warn(".net 4.6 not detected. Please install .net 4.6 and try again.");
                return;
            }

            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };

            _fluentCommandLineParser.Setup(arg => arg.File)
                .As('f')
                .WithDescription("File to process. Either this or -d is required");

            _fluentCommandLineParser.Setup(arg => arg.Directory)
                .As('d')
                .WithDescription("Directory to recursively process. Either this or -f is required");

            _fluentCommandLineParser.Setup(arg => arg.AllFiles)
                .As("all")
                .WithDescription(
                    "When true, process all files in directory vs. only files matching *.automaticDestinations-ms or *.customDestinations-ms\r\n")
                .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.CsvFile)
                .As("csv")
                .WithDescription(
                    "File to save CSV (tab separated) formatted results to. Be sure to include the full path in double quotes");

            _fluentCommandLineParser.Setup(arg => arg.XmlDirectory)
                .As("xml")
                .WithDescription(
                    "Directory to save XML formatted results to. Be sure to include the full path in double quotes");

            _fluentCommandLineParser.Setup(arg => arg.xHtmlDirectory)
                .As("html")
                .WithDescription(
                    "Directory to save xhtml formatted results to. Be sure to include the full path in double quotes");

            _fluentCommandLineParser.Setup(arg => arg.JsonDirectory)
                .As("json")
                .WithDescription(
                    "Directory to save json representation to. Use --pretty for a more human readable layout");

            _fluentCommandLineParser.Setup(arg => arg.JsonPretty)
                .As("pretty")
                .WithDescription(
                    "When exporting to json, use a more human readable layout").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Quiet)
                .As('q')
                .WithDescription(
                    "When true, only show the filename being processed vs all output. Useful to speed up exporting to json and/or csv\r\n")
                .SetDefault(false);


            _fluentCommandLineParser.Setup(arg => arg.IncludeLnkDetail).As("ld")
    .WithDescription(
        "When true, include more information about lnk files (for full lnk details, dump lnk entries and process with LECmd")
    .SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.LnkDumpDirectory).As("dumpTo")
    .WithDescription(
        "The directory to export all lnk files to")
    .SetDefault(string.Empty);


            //            _fluentCommandLineParser.Setup(arg => arg.NoTargetIDList)
            //                .As("nid")
            //                .WithDescription(
            //                    "When true, Target ID list details will NOT be displayed. Default is false.").SetDefault(false);
            //
            //
            //            _fluentCommandLineParser.Setup(arg => arg.NoExtraBlocks)
            //                .As("neb")
            //                .WithDescription(
            //                    "When true, Extra blocks information will NOT be displayed. Default is false.").SetDefault(false);


            var header =
                $"JLECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/JLECmd";

            var footer = @"Examples: JLECmd.exe -f ""C:\Temp\f01b4d95cf55d32a.customDestinations-ms""" + "\r\n\t " +
                         @" JLECmd.exe -f ""C:\Temp\f01b4d95cf55d32a.customDestinations-ms"" --json ""D:\jsonOutput"" --jsonpretty" + "\r\n\t " +
                         @" JLECmd.exe -d ""C:\CustomDestinations"" --csv ""c:\temp\jumplist_out.tsv"" --html c:\temp --xml c:\temp\xml -q" +
                         "\r\n\t " +
                         @" JLECmd.exe -d ""C:\Temp"" --all" + "\r\n\t" +
                         "\r\n\t" +
                         "  Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";

            _fluentCommandLineParser.SetupHelp("?", "help")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + footer));

            var result = _fluentCommandLineParser.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (result.HasErrors)
            {
                _logger.Error("");
                _logger.Error(result.ErrorText);

                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                return;
            }

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.File) &&
                UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.Directory))
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("Either -f or -d is required. Exiting");
                return;
            }

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.File) == false &&
                !File.Exists(_fluentCommandLineParser.Object.File))
            {
                _logger.Warn($"File '{_fluentCommandLineParser.Object.File}' not found. Exiting");
                return;
            }

            if (UsefulExtension.IsNullOrEmpty(_fluentCommandLineParser.Object.Directory) == false &&
                !Directory.Exists(_fluentCommandLineParser.Object.Directory))
            {
                _logger.Warn($"Directory '{_fluentCommandLineParser.Object.Directory}' not found. Exiting");
                return;
            }

            _logger.Info(header);
            _logger.Info("");
            _logger.Info($"Command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\r\n");


            _processedAutoFiles = new List<AutomaticDestination>();
            _processedCustomFiles = new List<CustomDestination>();


            if (_fluentCommandLineParser.Object.CsvFile?.Length > 0)
            {
                if (string.IsNullOrEmpty(Path.GetFileName(_fluentCommandLineParser.Object.CsvFile)))
                {
                    _logger.Error(
                        $"'{_fluentCommandLineParser.Object.CsvFile}' is not a file. Please specify a file to save results to. Exiting");
                    return;
                }
            }

            if (_fluentCommandLineParser.Object.File?.Length > 0)
            {
                if (IsAutomaticDestinationFile(_fluentCommandLineParser.Object.File))
                {
                    try
                    {
                        AutomaticDestination adjl = null;
                        adjl = ProcessAutoFile(_fluentCommandLineParser.Object.File);
                        if (adjl != null)
                        {
                            _processedAutoFiles.Add(adjl);
                        }
                    }
                    catch (UnauthorizedAccessException ua)
                    {
                        _logger.Error(
                            $"Unable to access '{_fluentCommandLineParser.Object.File}'. Are you running as an administrator? Error: {ua.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error getting jump lists. Error: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    try
                    {
                        CustomDestination cdjl = null;
                        cdjl = ProcessCustomFile(_fluentCommandLineParser.Object.File);
                        if (cdjl != null)
                        {
                            _processedCustomFiles.Add(cdjl);
                        }
                    }
                    catch (UnauthorizedAccessException ua)
                    {
                        _logger.Error(
                            $"Unable to access '{_fluentCommandLineParser.Object.File}'. Are you running as an administrator? Error: {ua.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(
                            $"Error getting jump lists. Error: {ex.Message}");
                        return;
                    }
                }
            }
            else
            {
                _logger.Info($"Looking for jump list files in '{_fluentCommandLineParser.Object.Directory}'");
                _logger.Info("");

                var jumpFiles = new List<string>();

                _failedFiles = new List<string>();

                try
                {
                    var mask = "*.*Destinations-ms";
                    if (_fluentCommandLineParser.Object.AllFiles)
                    {
                        mask = "*";
                    }

                    jumpFiles.AddRange(Directory.GetFiles(_fluentCommandLineParser.Object.Directory, mask,
                        SearchOption.AllDirectories));
                }
                catch (UnauthorizedAccessException ua)
                {
                    _logger.Error(
                        $"Unable to access '{_fluentCommandLineParser.Object.Directory}'. Error message: {ua.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"Error getting jump list files in '{_fluentCommandLineParser.Object.Directory}'. Error: {ex.Message}");
                    return;
                }

                _logger.Info($"Found {jumpFiles.Count:N0} jump lists files");
                _logger.Info("");

                var sw = new Stopwatch();
                sw.Start();

                foreach (var file in jumpFiles)
                {
                    if (IsAutomaticDestinationFile(file))
                    {
                        AutomaticDestination adjl = null;
                        adjl = ProcessAutoFile(file);
                        if (adjl != null)
                        {
                            _processedAutoFiles.Add(adjl);
                        }
                    }
                    else
                    {
                        CustomDestination cdjl = null;
                        cdjl = ProcessCustomFile(file);
                        if (cdjl != null)
                        {
                            _processedCustomFiles.Add(cdjl);
                        }
                    }
                }

                sw.Stop();

                if (_fluentCommandLineParser.Object.Quiet)
                {
                    _logger.Info("");
                }

                _logger.Info(
                    $"Processed {jumpFiles.Count - _failedFiles.Count:N0} out of {jumpFiles.Count:N0} files in {sw.Elapsed.TotalSeconds:N4} seconds");
                if (_failedFiles.Count > 0)
                {
                    _logger.Info("");
                    _logger.Warn("Failed files");
                    foreach (var failedFile in _failedFiles)
                    {
                        _logger.Info($"  {failedFile}");
                    }
                }
            }


            //export lnks if requested
            if (_fluentCommandLineParser.Object.LnkDumpDirectory.Length > 0)
            {
                _logger.Info("");
                _logger.Warn(
                          $"Dumping lnk files to '{_fluentCommandLineParser.Object.LnkDumpDirectory}'");

                if (Directory.Exists(_fluentCommandLineParser.Object.LnkDumpDirectory) == false)
                {
                    Directory.CreateDirectory(_fluentCommandLineParser.Object.LnkDumpDirectory);
                }

                foreach (var processedCustomFile in _processedCustomFiles)
                {
                    foreach (var entry in processedCustomFile.Entries)
                    {
                        if (entry.LnkFiles.Count == 0)
                        {
                            continue;
                        }

                        var outDir = Path.Combine(_fluentCommandLineParser.Object.LnkDumpDirectory,
                            Path.GetFileName(processedCustomFile.SourceFile));

                        if (Directory.Exists(outDir) == false)
                        {
                            Directory.CreateDirectory(outDir);
                        }

                        entry.DumpAllLnkFiles(outDir, processedCustomFile.AppId.AppId);
                    }
                }

                foreach (var automaticDestination in _processedAutoFiles)
                {
                    if (automaticDestination.DestListCount == 0)
                    {
                        continue;
                    }
                    var outDir = Path.Combine(_fluentCommandLineParser.Object.LnkDumpDirectory,
                           Path.GetFileName(automaticDestination.SourceFile));

                    if (Directory.Exists(outDir) == false)
                    {
                        Directory.CreateDirectory(outDir);
                    }

                    automaticDestination.DumpAllLnkFiles(outDir);
                }

                
            }


            throw new Exception("Do exporting");

            if (_processedAutoFiles.Count > 0)
            {
                _logger.Info("");

                try
                {
                    CsvWriter csv = null;
                    StreamWriter sw = null;

                    if (_fluentCommandLineParser.Object.CsvFile?.Length > 0)
                    {
                        _fluentCommandLineParser.Object.CsvFile =
                            Path.GetFullPath(_fluentCommandLineParser.Object.CsvFile);
                        _logger.Warn(
                            $"CSV (tab separated) output will be saved to '{_fluentCommandLineParser.Object.CsvFile}'");

                        try
                        {
                            sw = new StreamWriter(_fluentCommandLineParser.Object.CsvFile);
                            csv = new CsvWriter(sw);
                            csv.Configuration.Delimiter = $"{'\t'}";
                            csv.WriteHeader(typeof(CustomCsvOut));
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(
                                $"Unable to open '{_fluentCommandLineParser.Object.CsvFile}' for writing. CSV export canceled. Error: {ex.Message}");
                        }
                    }

                    if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
                    {
                        _logger.Warn($"Saving json output to '{_fluentCommandLineParser.Object.JsonDirectory}'");
                    }
                    if (_fluentCommandLineParser.Object.XmlDirectory?.Length > 0)
                    {
                        _logger.Warn($"Saving XML output to '{_fluentCommandLineParser.Object.XmlDirectory}'");
                    }

                    XmlTextWriter xml = null;

                    if (_fluentCommandLineParser.Object.xHtmlDirectory?.Length > 0)
                    {
                        var outDir = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory,
                            $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_LECmd_Output_for_{_fluentCommandLineParser.Object.xHtmlDirectory.Replace(@":\", "_").Replace(@"\", "_")}");

                        if (Directory.Exists(outDir) == false)
                        {
                            Directory.CreateDirectory(outDir);
                        }

//                        File.WriteAllText(Path.Combine(outDir, "normalize.css"), Resources.normalize);
//                        File.WriteAllText(Path.Combine(outDir, "style.css"), Resources.style);

                        var outFile = Path.Combine(_fluentCommandLineParser.Object.xHtmlDirectory, outDir, "index.xhtml");

                        _logger.Warn($"Saving HTML output to '{outFile}'");

                        xml = new XmlTextWriter(outFile, Encoding.UTF8)
                        {
                            Formatting = Formatting.Indented,
                            Indentation = 4
                        };

                        xml.WriteStartDocument();

                        xml.WriteProcessingInstruction("xml-stylesheet", "href=\"normalize.css\"");
                        xml.WriteProcessingInstruction("xml-stylesheet", "href=\"style.css\"");

                        xml.WriteStartElement("document");
                    }

//                    foreach (var processedFile in _processedAutoFiles)
//                    {
//                        var o = GetCsvFormat(processedFile);
//
//                        try
//                        {
//                            csv?.WriteRecord(o);
//                        }
//                        catch (Exception ex)
//                        {
//                            _logger.Error(
//                                $"Error writing record for '{processedFile.SourceFile}' to '{_fluentCommandLineParser.Object.CsvFile}'. Error: {ex.Message}");
//                        }
//
//                        if (_fluentCommandLineParser.Object.JsonDirectory?.Length > 0)
//                        {
//                            SaveJson(processedFile, _fluentCommandLineParser.Object.JsonPretty,
//                                _fluentCommandLineParser.Object.JsonDirectory);
//                        }
//
//                        //XHTML
//                        xml?.WriteStartElement("Container");
//                        xml?.WriteElementString("SourceFile", o.SourceFile);
//                        xml?.WriteElementString("SourceCreated", o.SourceCreated.ToString());
//                        xml?.WriteElementString("SourceModified", o.SourceModified.ToString());
//                        xml?.WriteElementString("SourceAccessed", o.SourceAccessed.ToString());
//                        xml?.WriteElementString("TargetCreated", o.TargetCreated.ToString());
//                        xml?.WriteElementString("TargetModified", o.TargetModified.ToString());
//                        xml?.WriteElementString("TargetAccessed", o.TargetModified.ToString());
//                        xml?.WriteElementString("FileSize", o.FileSize.ToString());
//                        xml?.WriteElementString("RelativePath", o.RelativePath);
//                        xml?.WriteElementString("WorkingDirectory", o.WorkingDirectory);
//                        xml?.WriteElementString("FileAttributes", o.FileAttributes);
//                        xml?.WriteElementString("HeaderFlags", o.HeaderFlags);
//                        xml?.WriteElementString("DriveType", o.DriveType);
//                        xml?.WriteElementString("DriveSerialNumber", o.DriveSerialNumber);
//                        xml?.WriteElementString("DriveLabel", o.DriveLabel);
//                        xml?.WriteElementString("LocalPath", o.LocalPath);
//                        xml?.WriteElementString("CommonPath", o.CommonPath);
//
//                        xml?.WriteElementString("TargetIDAbsolutePath", o.TargetIDAbsolutePath);
//
//                        xml?.WriteElementString("TargetMFTEntryNumber", $"{o.TargetMFTEntryNumber}");
//                        xml?.WriteElementString("TargetMFTSequenceNumber", $"{o.TargetMFTSequenceNumber}");
//
//                        xml?.WriteElementString("MachineID", o.MachineID);
//                        xml?.WriteElementString("MachineMACAddress", o.MachineMACAddress);
//                        xml?.WriteElementString("MACVendor", o.MACVendor);
//                        xml?.WriteElementString("TrackerCreatedOn", o.TrackerCreatedOn.ToString());
//
//                        xml?.WriteElementString("ExtraBlocksPresent", o.ExtraBlocksPresent);
//
//                        xml?.WriteEndElement();
//
//                        if (_fluentCommandLineParser.Object.XmlDirectory?.Length > 0)
//                        {
//                            SaveXML(o, _fluentCommandLineParser.Object.XmlDirectory);
//                        }
//                    }


                    //Close CSV stuff
                    sw?.Flush();
                    sw?.Close();

                    //Close XML
                    xml?.WriteEndElement();
                    xml?.WriteEndDocument();
                    xml?.Flush();
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"Error exporting data! Error: {ex.Message}");
                }
            }
        }

        private static bool IsAutomaticDestinationFile(string file)
        {
            const ulong signature = 0xe11ab1a1e011cfd0;

            var sig = BitConverter.ToUInt64(File.ReadAllBytes(file), 0);

            return signature == sig;
        }

        private static CustomCsvOut GetCsvFormat(LnkFile lnk)
        {
            var csOut = new CustomCsvOut
            {
                SourceFile = lnk.SourceFile,
                SourceCreated = lnk.SourceCreated.ToString(),
                SourceModified = lnk.SourceModified.ToString(),
                SourceAccessed = lnk.SourceAccessed.ToString(),
                TargetCreated =
                    lnk.Header.TargetCreationDate.Year == 1601 ? string.Empty : lnk.Header.TargetCreationDate.ToString(),
                TargetModified =
                    lnk.Header.TargetModificationDate.Year == 1601
                        ? string.Empty
                        : lnk.Header.TargetModificationDate.ToString(),
                TargetAccessed =
                    lnk.Header.TargetLastAccessedDate.Year == 1601
                        ? string.Empty
                        : lnk.Header.TargetLastAccessedDate.ToString(),
                CommonPath = lnk.CommonPath,
                DriveLabel = lnk.VolumeInfo?.VolumeLabel,
                DriveSerialNumber = lnk.VolumeInfo?.DriveSerialNumber,
                DriveType = lnk.VolumeInfo == null ? "(None)" : GetDescriptionFromEnumValue(lnk.VolumeInfo.DriveType),
                FileAttributes = lnk.Header.FileAttributes.ToString(),
                FileSize = lnk.Header.FileSize,
                HeaderFlags = lnk.Header.DataFlags.ToString(),
                LocalPath = lnk.LocalPath,
                RelativePath = lnk.RelativePath
            };

            if (lnk.TargetIDs?.Count > 0)
            {
                csOut.TargetIDAbsolutePath = GetAbsolutePathFromTargetIDs(lnk.TargetIDs);
            }

            csOut.WorkingDirectory = lnk.WorkingDirectory;

            var ebPresent = string.Empty;

            if (lnk.ExtraBlocks.Count > 0)
            {
                var names = new List<string>();

                foreach (var extraDataBase in lnk.ExtraBlocks)
                {
                    names.Add(extraDataBase.GetType().Name);
                }

                ebPresent = string.Join(", ", names);
            }

            csOut.ExtraBlocksPresent = ebPresent;

            var tnb = lnk.ExtraBlocks.SingleOrDefault(t => t.GetType().Name.ToUpper() == "TRACKERDATABASEBLOCK");


            if (tnb != null)
            {
                var tnbBlock = tnb as TrackerDataBaseBlock;

                csOut.TrackerCreatedOn = tnbBlock?.CreationTime.ToString();

                csOut.MachineID = tnbBlock?.MachineId;
                csOut.MachineMACAddress = tnbBlock?.MacAddress;
            }

            if (lnk.TargetIDs?.Count > 0)
            {
                var si = lnk.TargetIDs.Last();

                if (si.ExtensionBlocks?.Count > 0)
                {
                    var eb = si.ExtensionBlocks?.Last();
                    if (eb is Beef0004)
                    {
                        var eb4 = eb as Beef0004;
                        if (eb4.MFTInformation.MFTEntryNumber != null)
                        {
                            csOut.TargetMFTEntryNumber = $"0x{eb4.MFTInformation.MFTEntryNumber.Value.ToString("X")}";
                        }

                        if (eb4.MFTInformation.MFTSequenceNumber != null)
                        {
                            csOut.TargetMFTSequenceNumber =
                                $"0x{eb4.MFTInformation.MFTSequenceNumber.Value.ToString("X")}";
                        }
                    }
                }
            }


            return csOut;
        }

        private static void DumpToJson(LnkFile lnk, bool pretty, string outFile)
        {
            if (pretty)
            {
                File.WriteAllText(outFile, lnk.Dump());
            }
            else
            {
                File.WriteAllText(outFile, lnk.ToJson());
            }
        }

        private static void SaveXML(CustomCsvOut csout, string outDir)
        {
            try
            {
                if (Directory.Exists(outDir) == false)
                {
                    Directory.CreateDirectory(outDir);
                }

                var outName =
                    $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_{Path.GetFileName(csout.SourceFile)}.xml";
                var outFile = Path.Combine(outDir, outName);


                File.WriteAllText(outFile, csout.ToXml());
            }
            catch (Exception ex)
            {
                _logger.Error($"Error exporting XML for '{csout.SourceFile}'. Error: {ex.Message}");
            }
        }

        private static void SaveJson(LnkFile lnk, bool pretty, string outDir)
        {
            try
            {
                if (Directory.Exists(outDir) == false)
                {
                    Directory.CreateDirectory(outDir);
                }

                var outName =
                    $"{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}_{Path.GetFileName(lnk.SourceFile)}.json";
                var outFile = Path.Combine(outDir, outName);

                DumpToJson(lnk, pretty, outFile);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error exporting json for '{lnk.SourceFile}'. Error: {ex.Message}");
            }
        }

        private static string GetDescriptionFromEnumValue(Enum value)
        {
            var attribute = value.GetType()
                .GetField(value.ToString())
                .GetCustomAttributes(typeof(DescriptionAttribute), false)
                .SingleOrDefault() as DescriptionAttribute;
            return attribute == null ? value.ToString() : attribute.Description;
        }

        private static string GetAbsolutePathFromTargetIDs(List<ShellBag> ids)
        {
            var absPath = string.Empty;

            foreach (var shellBag in ids)
            {
                absPath += shellBag.Value + @"\";
            }

            absPath = absPath.Substring(0, absPath.Length - 1);

            return absPath;
        }

        private static AutomaticDestination ProcessAutoFile(string jlFile)
        {
            if (_fluentCommandLineParser.Object.Quiet == false)
            {
                _logger.Warn($"Processing '{jlFile}'");
                _logger.Info("");
            }

            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var autoDest = JumpList.JumpList.LoadAutoJumplist(jlFile);

                _logger.Error($"Source file: {autoDest.SourceFile}");

                _logger.Info("");

                _logger.Warn("--- AppId information ---");
                _logger.Warn($"AppID: {autoDest.AppId.AppId}, Description: {autoDest.AppId.Description}");

                _logger.Warn("--- DestList information ---");
                _logger.Info($"  Expected DestList entries:  {autoDest.DestListCount}");
                _logger.Info($"  Actual DestList entries {autoDest.DestListCount.ToString("N0")}");
                _logger.Info($"  DestList version: {autoDest.DestListVersion}");


                _logger.Info("");

                _logger.Warn("--- DestList entries ---");
                foreach (var autoDestList in autoDest.DestListEntries)
                {
                    _logger.Info($"Entry #: {autoDestList.EntryNumber}");
                    _logger.Info($"  Path: {autoDestList.Path}");
                    _logger.Info($"  Created on: {autoDestList.CreatedOn}");
                    _logger.Info($"  Last modified: {autoDestList.LastModified}");
                    _logger.Info($"  Hostname: {autoDestList.Hostname}");
                    _logger.Info($"  Mac Address: {autoDestList.MacAddress}");
                    _logger.Info($"  Lnk target created: {autoDestList.Lnk.Header.TargetCreationDate}");
                    _logger.Info($"  Lnk target modified: {autoDestList.Lnk.Header.TargetModificationDate}");
                    _logger.Info($"  Lnk target accessed: {autoDestList.Lnk.Header.TargetLastAccessedDate}");

                    _logger.Error("  (More info will be included here)");

                    _logger.Info("");
                }


                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Error($"Source file: {autoDest.SourceFile}");
                //                    _logger.Info($"  Source created:  {autoDest.SourceCreated}");
                //                    _logger.Info($"  Source modified: {autoDest.SourceModified}");
                //                    _logger.Info($"  Source accessed: {autoDest.SourceAccessed}");
                //                    _logger.Info("");
                //
                //                    _logger.Warn("--- Header ---");
                //
                //                    var tc = autoDest.Header.TargetCreationDate.Year == 1601 ? "" : autoDest.Header.TargetCreationDate.ToString();
                //                    var tm = autoDest.Header.TargetModificationDate.Year == 1601 ? "" : autoDest.Header.TargetModificationDate.ToString();
                //                    var ta = autoDest.Header.TargetLastAccessedDate.Year == 1601 ? "" : autoDest.Header.TargetLastAccessedDate.ToString();
                //
                //                    _logger.Info($"  Target created:  {tc}");
                //                    _logger.Info($"  Target modified: {tm}");
                //                    _logger.Info($"  Target accessed: {ta}");
                //                    _logger.Info("");
                //                    _logger.Info($"  File size: {autoDest.Header.FileSize:N0}");
                //                    _logger.Info($"  Flags: {autoDest.Header.DataFlags}");
                //                    _logger.Info($"  File attributes: {autoDest.Header.FileAttributes}");
                //
                //                    if (autoDest.Header.HotKey.Length > 0)
                //                    {
                //                        _logger.Info($"  Hot key: {autoDest.Header.HotKey}");
                //                    }
                //
                //                    _logger.Info($"  Icon index: {autoDest.Header.IconIndex}");
                //                    _logger.Info(
                //                        $"  Show window: {autoDest.Header.ShowWindow} ({GetDescriptionFromEnumValue(autoDest.Header.ShowWindow)})");
                //
                //                    _logger.Info("");
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasName) == Header.DataFlag.HasName)
                //                    {
                //                        _logger.Info($"Name: {autoDest.Name}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasRelativePath) == Header.DataFlag.HasRelativePath)
                //                    {
                //                        _logger.Info($"Relative Path: {autoDest.RelativePath}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasWorkingDir) == Header.DataFlag.HasWorkingDir)
                //                    {
                //                        _logger.Info($"Working Directory: {autoDest.WorkingDirectory}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasArguments) == Header.DataFlag.HasArguments)
                //                    {
                //                        _logger.Info($"Arguments: {autoDest.Arguments}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasIconLocation) == Header.DataFlag.HasIconLocation)
                //                    {
                //                        _logger.Info($"Icon Location: {autoDest.IconLocation}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasLinkInfo) == Header.DataFlag.HasLinkInfo)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Link information ---");
                //                        _logger.Info($"Flags: {autoDest.LocationFlags}");
                //
                //                        if (autoDest.VolumeInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn(">>Volume information");
                //                            _logger.Info($"  Drive type: {GetDescriptionFromEnumValue(autoDest.VolumeInfo.DriveType)}");
                //                            _logger.Info($"  Serial number: {autoDest.VolumeInfo.DriveSerialNumber}");
                //
                //                            var label = autoDest.VolumeInfo.VolumeLabel.Length > 0
                //                                ? autoDest.VolumeInfo.VolumeLabel
                //                                : "(No label)";
                //
                //                            _logger.Info($"  Label: {label}");
                //                        }
                //
                //                        if (autoDest.NetworkShareInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn("  Network share information");
                //
                //                            if (autoDest.NetworkShareInfo.DeviceName.Length > 0)
                //                            {
                //                                _logger.Info($"    Device name: {autoDest.NetworkShareInfo.DeviceName}");
                //                            }
                //
                //                            _logger.Info($"    Share name: {autoDest.NetworkShareInfo.NetworkShareName}");
                //
                //                            _logger.Info($"    Provider type: {autoDest.NetworkShareInfo.NetworkProviderType}");
                //                            _logger.Info($"    Share flags: {autoDest.NetworkShareInfo.ShareFlags}");
                //                            _logger.Info("");
                //                        }
                //
                //                        if (autoDest.LocalPath?.Length > 0)
                //                        {
                //                            _logger.Info($"  Local path: {autoDest.LocalPath}");
                //                        }
                //
                //                        if (autoDest.CommonPath.Length > 0)
                //                        {
                //                            _logger.Info($"  Common path: {autoDest.CommonPath}");
                //                        }
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn($"(Target ID information suppressed. Lnk TargetID count: {autoDest.TargetIDs.Count:N0})");
                //                    }
                //
                //                    if (autoDest.TargetIDs.Count > 0 && !_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //
                //                        var absPath = string.Empty;
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            absPath += shellBag.Value + @"\";
                //                        }
                //
                //                        _logger.Error("--- Target ID information (Format: Type ==> Value) ---");
                //                        _logger.Info("");
                //                        _logger.Info($"  Absolute path: {GetAbsolutePathFromTargetIDs(autoDest.TargetIDs)}");
                //                        _logger.Info("");
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            //HACK
                //                            //This is a total hack until i can refactor some shellbag code to clean things up
                //
                //                            var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //
                //                            _logger.Info($"  -{shellBag.FriendlyName} ==> {val}");
                //
                //                            switch (shellBag.GetType().Name.ToUpper())
                //                            {
                //                                case "SHELLBAG0X32":
                //                                    var b32 = shellBag as ShellBag0X32;
                //
                //                                    _logger.Info($"    Short name: {b32.ShortName}");
                //                                    _logger.Info($"    Modified: {b32.LastModificationTime}");
                //
                //                                    var extensionNumber32 = 0;
                //                                    if (b32.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b32.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b32.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber32:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber32 += 1;
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X31":
                //
                //                                    var b3x = shellBag as ShellBag0X31;
                //
                //                                    _logger.Info($"    Short name: {b3x.ShortName}");
                //                                    _logger.Info($"    Modified: {b3x.LastModificationTime}");
                //
                //                                    var extensionNumber = 0;
                //                                    if (b3x.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b3x.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b3x.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber += 1;
                //                                        }
                //                                    }
                //                                    break;
                //
                //                                case "SHELLBAG0X00":
                //                                    var b00 = shellBag as ShellBag0X00;
                //
                //                                    if (b00.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b00.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X01":
                //                                    var baaaa1f = shellBag as ShellBag0X01;
                //                                    if (baaaa1f.DriveLetter.Length > 0)
                //                                    {
                //                                        _logger.Info($"  Drive letter: {baaaa1f.DriveLetter}");
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0X1F":
                //
                //                                    var b1f = shellBag as ShellBag0X1F;
                //
                //                                    if (b1f.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b1f.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X2E":
                //                                    break;
                //                                case "SHELLBAG0X2F":
                //                                    var b2f = shellBag as ShellBag0X2F;
                //
                //                                    break;
                //                                case "SHELLBAG0X40":
                //                                    break;
                //                                case "SHELLBAG0X61":
                //
                //                                    break;
                //                                case "SHELLBAG0X71":
                //                                    var b71 = shellBag as ShellBag0X71;
                //                                    if (b71.PropertyStore?.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Fatal(
                //                                            "Property stores found! Please email lnk file to saericzimmerman@gmail.com so support can be added!!");
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X74":
                //                                    var b74 = shellBag as ShellBag0X74;
                //
                //                                    _logger.Info($"    Modified: {b74.LastModificationTime}");
                //
                //                                    var extensionNumber74 = 0;
                //                                    if (b74.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b74.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b74.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber74:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber74 += 1;
                //                                        }
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0XC3":
                //                                    break;
                //                                case "SHELLBAGZIPCONTENTS":
                //                                    break;
                //                                default:
                //                                    _logger.Fatal(
                //                                        $">> UNMAPPED Type! Please email lnk file to saericzimmerman@gmail.com so support can be added!");
                //                                    _logger.Fatal($">>{shellBag}");
                //                                    break;
                //                            }
                //
                //                            _logger.Info("");
                //                        }
                //                        _logger.Error("--- End Target ID information ---");
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn(
                //                            $"(Extra blocks information suppressed. Lnk Extra block count: {autoDest.ExtraBlocks.Count:N0})");
                //                    }
                //
                //                    if (autoDest.ExtraBlocks.Count > 0 && !_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Extra blocks information ---");
                //                        _logger.Info("");
                //
                //                        foreach (var extraDataBase in autoDest.ExtraBlocks)
                //                        {
                //                            switch (extraDataBase.GetType().Name)
                //                            {
                //                                case "ConsoleDataBlock":
                //                                    var cdb = extraDataBase as ConsoleDataBlock;
                //                                    _logger.Warn(">> Console data block");
                //                                    _logger.Info($"   Fill Attributes: {cdb.FillAttributes}");
                //                                    _logger.Info($"   Popup Attributes: {cdb.PopupFillAttributes}");
                //                                    _logger.Info(
                //                                        $"   Buffer Size (Width x Height): {cdb.ScreenWidthBufferSize} x {cdb.ScreenHeightBufferSize}");
                //                                    _logger.Info(
                //                                        $"   Window Size (Width x Height): {cdb.WindowWidth} x {cdb.WindowHeight}");
                //                                    _logger.Info($"   Origin (X/Y): {cdb.WindowOriginX}/{cdb.WindowOriginY}");
                //                                    _logger.Info($"   Font Size: {cdb.FontSize}");
                //                                    _logger.Info($"   Is Bold: {cdb.IsBold}");
                //                                    _logger.Info($"   Face Name: {cdb.FaceName}");
                //                                    _logger.Info($"   Cursor Size: {cdb.CursorSize}");
                //                                    _logger.Info($"   Is Full Screen: {cdb.IsFullScreen}");
                //                                    _logger.Info($"   Is Quick Edit: {cdb.IsQuickEdit}");
                //                                    _logger.Info($"   Is Insert Mode: {cdb.IsInsertMode}");
                //                                    _logger.Info($"   Is Auto Positioned: {cdb.IsAutoPositioned}");
                //                                    _logger.Info($"   History Buffer Size: {cdb.HistoryBufferSize}");
                //                                    _logger.Info($"   History Buffer Count: {cdb.HistoryBufferCount}");
                //                                    _logger.Info($"   History Duplicates Allowed: {cdb.HistoryDuplicatesAllowed}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ConsoleFEDataBlock":
                //                                    var cfedb = extraDataBase as ConsoleFeDataBlock;
                //                                    _logger.Warn(">> Console FE data block");
                //                                    _logger.Info($"   Code page: {cfedb.CodePage}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "DarwinDataBlock":
                //                                    var ddb = extraDataBase as DarwinDataBlock;
                //                                    _logger.Warn(">> Darwin data block");
                //                                    _logger.Info($"   Application ID: {ddb.ApplicationIdentifierUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "EnvironmentVariableDataBlock":
                //                                    var evdb = extraDataBase as EnvironmentVariableDataBlock;
                //                                    _logger.Warn(">> Environment variable data block");
                //                                    _logger.Info($"   Environment variables: {evdb.EnvironmentVariablesUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "IconEnvironmentDataBlock":
                //                                    var iedb = extraDataBase as IconEnvironmentDataBlock;
                //                                    _logger.Warn(">> Icon environment data block");
                //                                    _logger.Info($"   Icon path: {iedb.IconPathUni}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "KnownFolderDataBlock":
                //                                    var kfdb = extraDataBase as KnownFolderDataBlock;
                //                                    _logger.Warn(">> Known folder data block");
                //                                    _logger.Info(
                //                                        $"   Known folder GUID: {kfdb.KnownFolderId} ==> {kfdb.KnownFolderName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "PropertyStoreDataBlock":
                //                                    var psdb = extraDataBase as PropertyStoreDataBlock;
                //
                //                                    if (psdb.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn(
                //                                            ">> Property store data block (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in psdb.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"   {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("   (Property store is empty)");
                //                                        }
                //                                    }
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ShimDataBlock":
                //                                    var sdb = extraDataBase as ShimDataBlock;
                //                                    _logger.Warn(">> Shimcache data block");
                //                                    _logger.Info($"   LayerName: {sdb.LayerName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "SpecialFolderDataBlock":
                //                                    var sfdb = extraDataBase as SpecialFolderDataBlock;
                //                                    _logger.Warn(">> Special folder data block");
                //                                    _logger.Info($"   Special Folder ID: {sfdb.SpecialFolderId}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "TrackerDataBaseBlock":
                //                                    var tdb = extraDataBase as TrackerDataBaseBlock;
                //                                    _logger.Warn(">> Tracker database block");
                //                                    _logger.Info($"   Machine ID: {tdb.MachineId}");
                //                                    _logger.Info($"   MAC Address: {tdb.MacAddress}");
                //                                    _logger.Info($"   MAC Vendor: {GetVendorFromMac(tdb.MacAddress)}");
                //                                    _logger.Info($"   Creation: {tdb.CreationTime}");
                //                                    _logger.Info("");
                //                                    _logger.Info($"   Volume Droid: {tdb.VolumeDroid}");
                //                                    _logger.Info($"   Volume Droid Birth: {tdb.VolumeDroidBirth}");
                //                                    _logger.Info($"   File Droid: {tdb.FileDroid}");
                //                                    _logger.Info($"   File Droid birth: {tdb.FileDroidBirth}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "VistaAndAboveIDListDataBlock":
                //                                    var vdb = extraDataBase as VistaAndAboveIdListDataBlock;
                //                                    _logger.Warn(">> Vista and above ID List data block");
                //
                //                                    foreach (var shellBag in vdb.TargetIDs)
                //                                    {
                //                                        var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //                                        _logger.Info($"   {shellBag.FriendlyName} ==> {val}");
                //                                    }
                //
                //                                    _logger.Info("");
                //                                    break;
                //                            }
                //                        }
                //                    }
                //                }
                //
                //                sw.Stop();
                //
                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Info("");
                //                }
                //
                //                _logger.Info(
                //                    $"---------- Processed '{autoDest.SourceFile}' in {sw.Elapsed.TotalSeconds:N8} seconds ----------");
                //
                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Info("\r\n");
                //                }

                return autoDest;
            }

            catch (Exception ex)
            {
                _failedFiles.Add($"{jlFile} ==> ({ex.Message})");
                _logger.Fatal($"Error opening '{jlFile}'. Message: {ex.Message}");
                _logger.Info("");
            }

            return null;
        }

        private static CustomDestination ProcessCustomFile(string jlFile)
        {
            if (_fluentCommandLineParser.Object.Quiet == false)
            {
                _logger.Warn($"Processing '{jlFile}'");
                _logger.Info("");
            }

            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var customDest = JumpList.JumpList.LoadCustomJumplist(jlFile);

                _logger.Error($"Source file: {customDest.SourceFile}");

                _logger.Info("");

                _logger.Warn("--- AppId information ---");
                _logger.Warn($"AppID: {customDest.AppId.AppId}, Description: {customDest.AppId.Description}");
                _logger.Warn("--- DestList information ---");
                _logger.Info($"  Entries:  {customDest.Entries.Count:N0}");


                var entryNum = 0;
                foreach (var entry in customDest.Entries)
                {
                    _logger.Warn($"    Entry #: {entryNum}, Rank: {entry.Rank}");

                    if (entry.Name.Length > 0)
                    {
                        _logger.Info($"   Name: {entry.Name}");
                    }

                    _logger.Info($"    Total lnk count: {entry.LnkFiles.Count}");

                    foreach (var lnkFile in entry.LnkFiles)
                    {
                        _logger.Info($"     lnk header flags: {lnkFile.Header.DataFlags}");
                        _logger.Info($"      Target created: {lnkFile.Header.TargetCreationDate}");
                        _logger.Info($"      Target modified: {lnkFile.Header.TargetModificationDate}");
                        _logger.Info($"      Target accessed: {lnkFile.Header.TargetLastAccessedDate}");
                    }
                    _logger.Info("");
                    entryNum += 1;


                    

                }


                
                

//                _logger.Warn("--- DestList entries ---");
//                foreach (var autoDestList in customDest.DestList)
//                {
//                    _logger.Info($"Entry #: {autoDestList.EntryNumber}");
//                    _logger.Info($"  Path: {autoDestList.Path}");
//                    _logger.Info($"  Created on: {autoDestList.CreatedOn}");
//                    _logger.Info($"  Last modified: {autoDestList.LastModified}");
//                    _logger.Info($"  Hostname: {autoDestList.Hostname}");
//                    _logger.Info($"  Mac Address: {autoDestList.MacAddress}");
//                    _logger.Info($"  Lnk target created: {autoDestList.Lnk.Header.TargetCreationDate}");
//                    _logger.Info($"  Lnk target modified: {autoDestList.Lnk.Header.TargetModificationDate}");
//                    _logger.Info($"  Lnk target accessed: {autoDestList.Lnk.Header.TargetLastAccessedDate}");
//
//                    _logger.Error("  (More info will be included here)");
//
//                    _logger.Info("");
//                }


                //                if (DestList != null)
                //                {
                //                    DestListCount = DestList.Header.NumberOfEntries;
                //                    PinnedDestListCount = DestList.Header.NumberOfPinnedEntries;
                //                    DestListVersion = DestList.Header.Version;
                //
                //                    foreach (var entry in DestList.Entries)
                //                    {
                //
                //                        var dirItem =
                //                                        _oleContainer.Directory.SingleOrDefault(
                //                                            t => t.DirectoryName.ToLowerInvariant() == entry.EntryNumber.ToString("X").ToLowerInvariant());
                //
                //                        if (dirItem != null)
                //                        {
                //                            var p = _oleContainer.GetPayloadForDirectory(dirItem);
                //
                //                            var dlnk = new LnkFile(p, $"{sourceFile}__Directory name {entry.EntryNumber}");
                //
                //                            var dl = new AutoDestList(entry, dlnk);
                //
                //                            DestList.Add(dl);
                //                        }
                //
                //
                //
                //                    }
                //                }


                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Error($"Source file: {autoDest.SourceFile}");
                //                    _logger.Info($"  Source created:  {autoDest.SourceCreated}");
                //                    _logger.Info($"  Source modified: {autoDest.SourceModified}");
                //                    _logger.Info($"  Source accessed: {autoDest.SourceAccessed}");
                //                    _logger.Info("");
                //
                //                    _logger.Warn("--- Header ---");
                //
                //                    var tc = autoDest.Header.TargetCreationDate.Year == 1601 ? "" : autoDest.Header.TargetCreationDate.ToString();
                //                    var tm = autoDest.Header.TargetModificationDate.Year == 1601 ? "" : autoDest.Header.TargetModificationDate.ToString();
                //                    var ta = autoDest.Header.TargetLastAccessedDate.Year == 1601 ? "" : autoDest.Header.TargetLastAccessedDate.ToString();
                //
                //                    _logger.Info($"  Target created:  {tc}");
                //                    _logger.Info($"  Target modified: {tm}");
                //                    _logger.Info($"  Target accessed: {ta}");
                //                    _logger.Info("");
                //                    _logger.Info($"  File size: {autoDest.Header.FileSize:N0}");
                //                    _logger.Info($"  Flags: {autoDest.Header.DataFlags}");
                //                    _logger.Info($"  File attributes: {autoDest.Header.FileAttributes}");
                //
                //                    if (autoDest.Header.HotKey.Length > 0)
                //                    {
                //                        _logger.Info($"  Hot key: {autoDest.Header.HotKey}");
                //                    }
                //
                //                    _logger.Info($"  Icon index: {autoDest.Header.IconIndex}");
                //                    _logger.Info(
                //                        $"  Show window: {autoDest.Header.ShowWindow} ({GetDescriptionFromEnumValue(autoDest.Header.ShowWindow)})");
                //
                //                    _logger.Info("");
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasName) == Header.DataFlag.HasName)
                //                    {
                //                        _logger.Info($"Name: {autoDest.Name}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasRelativePath) == Header.DataFlag.HasRelativePath)
                //                    {
                //                        _logger.Info($"Relative Path: {autoDest.RelativePath}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasWorkingDir) == Header.DataFlag.HasWorkingDir)
                //                    {
                //                        _logger.Info($"Working Directory: {autoDest.WorkingDirectory}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasArguments) == Header.DataFlag.HasArguments)
                //                    {
                //                        _logger.Info($"Arguments: {autoDest.Arguments}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasIconLocation) == Header.DataFlag.HasIconLocation)
                //                    {
                //                        _logger.Info($"Icon Location: {autoDest.IconLocation}");
                //                    }
                //
                //                    if ((autoDest.Header.DataFlags & Header.DataFlag.HasLinkInfo) == Header.DataFlag.HasLinkInfo)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Link information ---");
                //                        _logger.Info($"Flags: {autoDest.LocationFlags}");
                //
                //                        if (autoDest.VolumeInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn(">>Volume information");
                //                            _logger.Info($"  Drive type: {GetDescriptionFromEnumValue(autoDest.VolumeInfo.DriveType)}");
                //                            _logger.Info($"  Serial number: {autoDest.VolumeInfo.DriveSerialNumber}");
                //
                //                            var label = autoDest.VolumeInfo.VolumeLabel.Length > 0
                //                                ? autoDest.VolumeInfo.VolumeLabel
                //                                : "(No label)";
                //
                //                            _logger.Info($"  Label: {label}");
                //                        }
                //
                //                        if (autoDest.NetworkShareInfo != null)
                //                        {
                //                            _logger.Info("");
                //                            _logger.Warn("  Network share information");
                //
                //                            if (autoDest.NetworkShareInfo.DeviceName.Length > 0)
                //                            {
                //                                _logger.Info($"    Device name: {autoDest.NetworkShareInfo.DeviceName}");
                //                            }
                //
                //                            _logger.Info($"    Share name: {autoDest.NetworkShareInfo.NetworkShareName}");
                //
                //                            _logger.Info($"    Provider type: {autoDest.NetworkShareInfo.NetworkProviderType}");
                //                            _logger.Info($"    Share flags: {autoDest.NetworkShareInfo.ShareFlags}");
                //                            _logger.Info("");
                //                        }
                //
                //                        if (autoDest.LocalPath?.Length > 0)
                //                        {
                //                            _logger.Info($"  Local path: {autoDest.LocalPath}");
                //                        }
                //
                //                        if (autoDest.CommonPath.Length > 0)
                //                        {
                //                            _logger.Info($"  Common path: {autoDest.CommonPath}");
                //                        }
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn($"(Target ID information suppressed. Lnk TargetID count: {autoDest.TargetIDs.Count:N0})");
                //                    }
                //
                //                    if (autoDest.TargetIDs.Count > 0 && !_fluentCommandLineParser.Object.NoTargetIDList)
                //                    {
                //                        _logger.Info("");
                //
                //                        var absPath = string.Empty;
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            absPath += shellBag.Value + @"\";
                //                        }
                //
                //                        _logger.Error("--- Target ID information (Format: Type ==> Value) ---");
                //                        _logger.Info("");
                //                        _logger.Info($"  Absolute path: {GetAbsolutePathFromTargetIDs(autoDest.TargetIDs)}");
                //                        _logger.Info("");
                //
                //                        foreach (var shellBag in autoDest.TargetIDs)
                //                        {
                //                            //HACK
                //                            //This is a total hack until i can refactor some shellbag code to clean things up
                //
                //                            var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //
                //                            _logger.Info($"  -{shellBag.FriendlyName} ==> {val}");
                //
                //                            switch (shellBag.GetType().Name.ToUpper())
                //                            {
                //                                case "SHELLBAG0X32":
                //                                    var b32 = shellBag as ShellBag0X32;
                //
                //                                    _logger.Info($"    Short name: {b32.ShortName}");
                //                                    _logger.Info($"    Modified: {b32.LastModificationTime}");
                //
                //                                    var extensionNumber32 = 0;
                //                                    if (b32.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b32.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b32.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber32:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber32 += 1;
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X31":
                //
                //                                    var b3x = shellBag as ShellBag0X31;
                //
                //                                    _logger.Info($"    Short name: {b3x.ShortName}");
                //                                    _logger.Info($"    Modified: {b3x.LastModificationTime}");
                //
                //                                    var extensionNumber = 0;
                //                                    if (b3x.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b3x.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b3x.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber += 1;
                //                                        }
                //                                    }
                //                                    break;
                //
                //                                case "SHELLBAG0X00":
                //                                    var b00 = shellBag as ShellBag0X00;
                //
                //                                    if (b00.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b00.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X01":
                //                                    var baaaa1f = shellBag as ShellBag0X01;
                //                                    if (baaaa1f.DriveLetter.Length > 0)
                //                                    {
                //                                        _logger.Info($"  Drive letter: {baaaa1f.DriveLetter}");
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0X1F":
                //
                //                                    var b1f = shellBag as ShellBag0X1F;
                //
                //                                    if (b1f.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn("  >> Property store (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in b1f.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"     {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("     (Property store is empty)");
                //                                        }
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X2E":
                //                                    break;
                //                                case "SHELLBAG0X2F":
                //                                    var b2f = shellBag as ShellBag0X2F;
                //
                //                                    break;
                //                                case "SHELLBAG0X40":
                //                                    break;
                //                                case "SHELLBAG0X61":
                //
                //                                    break;
                //                                case "SHELLBAG0X71":
                //                                    var b71 = shellBag as ShellBag0X71;
                //                                    if (b71.PropertyStore?.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Fatal(
                //                                            "Property stores found! Please email lnk file to saericzimmerman@gmail.com so support can be added!!");
                //                                    }
                //
                //                                    break;
                //                                case "SHELLBAG0X74":
                //                                    var b74 = shellBag as ShellBag0X74;
                //
                //                                    _logger.Info($"    Modified: {b74.LastModificationTime}");
                //
                //                                    var extensionNumber74 = 0;
                //                                    if (b74.ExtensionBlocks.Count > 0)
                //                                    {
                //                                        _logger.Info($"    Extension block count: {b74.ExtensionBlocks.Count:N0}");
                //                                        _logger.Info("");
                //                                        foreach (var extensionBlock in b74.ExtensionBlocks)
                //                                        {
                //                                            _logger.Info(
                //                                                $"    --------- Block {extensionNumber74:N0} ({extensionBlock.GetType().Name}) ---------");
                //                                            if (extensionBlock is Beef0004)
                //                                            {
                //                                                var b4 = extensionBlock as Beef0004;
                //
                //                                                _logger.Info($"    Long name: {b4.LongName}");
                //                                                if (b4.LocalisedName.Length > 0)
                //                                                {
                //                                                    _logger.Info($"    Localized name: {b4.LocalisedName}");
                //                                                }
                //
                //                                                _logger.Info($"    Created: {b4.CreatedOnTime}");
                //                                                _logger.Info($"    Last access: {b4.LastAccessTime}");
                //                                                if (b4.MFTInformation.MFTEntryNumber > 0)
                //                                                {
                //                                                    _logger.Info(
                //                                                        $"    MFT entry/sequence #: {b4.MFTInformation.MFTEntryNumber}/{b4.MFTInformation.MFTSequenceNumber} (0x{b4.MFTInformation.MFTEntryNumber:X}/0x{b4.MFTInformation.MFTSequenceNumber:X})");
                //                                                }
                //                                            }
                //                                            else if (extensionBlock is Beef0025)
                //                                            {
                //                                                var b25 = extensionBlock as Beef0025;
                //                                                _logger.Info(
                //                                                    $"    Filetime 1: {b25.FileTime1}, Filetime 2: {b25.FileTime2}");
                //                                            }
                //                                            else if (extensionBlock is Beef0003)
                //                                            {
                //                                                var b3 = extensionBlock as Beef0003;
                //                                                _logger.Info($"    GUID: {b3.GUID1} ({b3.GUID1Folder})");
                //                                            }
                //                                            else
                //                                            {
                //                                                _logger.Info($"    {extensionBlock}");
                //                                            }
                //
                //                                            extensionNumber74 += 1;
                //                                        }
                //                                    }
                //                                    break;
                //                                case "SHELLBAG0XC3":
                //                                    break;
                //                                case "SHELLBAGZIPCONTENTS":
                //                                    break;
                //                                default:
                //                                    _logger.Fatal(
                //                                        $">> UNMAPPED Type! Please email lnk file to saericzimmerman@gmail.com so support can be added!");
                //                                    _logger.Fatal($">>{shellBag}");
                //                                    break;
                //                            }
                //
                //                            _logger.Info("");
                //                        }
                //                        _logger.Error("--- End Target ID information ---");
                //                    }
                //
                //                    if (_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Warn(
                //                            $"(Extra blocks information suppressed. Lnk Extra block count: {autoDest.ExtraBlocks.Count:N0})");
                //                    }
                //
                //                    if (autoDest.ExtraBlocks.Count > 0 && !_fluentCommandLineParser.Object.NoExtraBlocks)
                //                    {
                //                        _logger.Info("");
                //                        _logger.Error("--- Extra blocks information ---");
                //                        _logger.Info("");
                //
                //                        foreach (var extraDataBase in autoDest.ExtraBlocks)
                //                        {
                //                            switch (extraDataBase.GetType().Name)
                //                            {
                //                                case "ConsoleDataBlock":
                //                                    var cdb = extraDataBase as ConsoleDataBlock;
                //                                    _logger.Warn(">> Console data block");
                //                                    _logger.Info($"   Fill Attributes: {cdb.FillAttributes}");
                //                                    _logger.Info($"   Popup Attributes: {cdb.PopupFillAttributes}");
                //                                    _logger.Info(
                //                                        $"   Buffer Size (Width x Height): {cdb.ScreenWidthBufferSize} x {cdb.ScreenHeightBufferSize}");
                //                                    _logger.Info(
                //                                        $"   Window Size (Width x Height): {cdb.WindowWidth} x {cdb.WindowHeight}");
                //                                    _logger.Info($"   Origin (X/Y): {cdb.WindowOriginX}/{cdb.WindowOriginY}");
                //                                    _logger.Info($"   Font Size: {cdb.FontSize}");
                //                                    _logger.Info($"   Is Bold: {cdb.IsBold}");
                //                                    _logger.Info($"   Face Name: {cdb.FaceName}");
                //                                    _logger.Info($"   Cursor Size: {cdb.CursorSize}");
                //                                    _logger.Info($"   Is Full Screen: {cdb.IsFullScreen}");
                //                                    _logger.Info($"   Is Quick Edit: {cdb.IsQuickEdit}");
                //                                    _logger.Info($"   Is Insert Mode: {cdb.IsInsertMode}");
                //                                    _logger.Info($"   Is Auto Positioned: {cdb.IsAutoPositioned}");
                //                                    _logger.Info($"   History Buffer Size: {cdb.HistoryBufferSize}");
                //                                    _logger.Info($"   History Buffer Count: {cdb.HistoryBufferCount}");
                //                                    _logger.Info($"   History Duplicates Allowed: {cdb.HistoryDuplicatesAllowed}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ConsoleFEDataBlock":
                //                                    var cfedb = extraDataBase as ConsoleFeDataBlock;
                //                                    _logger.Warn(">> Console FE data block");
                //                                    _logger.Info($"   Code page: {cfedb.CodePage}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "DarwinDataBlock":
                //                                    var ddb = extraDataBase as DarwinDataBlock;
                //                                    _logger.Warn(">> Darwin data block");
                //                                    _logger.Info($"   Application ID: {ddb.ApplicationIdentifierUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "EnvironmentVariableDataBlock":
                //                                    var evdb = extraDataBase as EnvironmentVariableDataBlock;
                //                                    _logger.Warn(">> Environment variable data block");
                //                                    _logger.Info($"   Environment variables: {evdb.EnvironmentVariablesUnicode}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "IconEnvironmentDataBlock":
                //                                    var iedb = extraDataBase as IconEnvironmentDataBlock;
                //                                    _logger.Warn(">> Icon environment data block");
                //                                    _logger.Info($"   Icon path: {iedb.IconPathUni}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "KnownFolderDataBlock":
                //                                    var kfdb = extraDataBase as KnownFolderDataBlock;
                //                                    _logger.Warn(">> Known folder data block");
                //                                    _logger.Info(
                //                                        $"   Known folder GUID: {kfdb.KnownFolderId} ==> {kfdb.KnownFolderName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "PropertyStoreDataBlock":
                //                                    var psdb = extraDataBase as PropertyStoreDataBlock;
                //
                //                                    if (psdb.PropertyStore.Sheets.Count > 0)
                //                                    {
                //                                        _logger.Warn(
                //                                            ">> Property store data block (Format: GUID\\ID Description ==> Value)");
                //                                        var propCount = 0;
                //
                //                                        foreach (var prop in psdb.PropertyStore.Sheets)
                //                                        {
                //                                            foreach (var propertyName in prop.PropertyNames)
                //                                            {
                //                                                propCount += 1;
                //
                //                                                var prefix = $"{prop.GUID}\\{propertyName.Key}".PadRight(43);
                //                                                var suffix =
                //                                                    $"{Utils.GetDescriptionFromGuidAndKey(prop.GUID, int.Parse(propertyName.Key))}"
                //                                                        .PadRight(35);
                //
                //                                                _logger.Info($"   {prefix} {suffix} ==> {propertyName.Value}");
                //                                            }
                //                                        }
                //
                //                                        if (propCount == 0)
                //                                        {
                //                                            _logger.Warn("   (Property store is empty)");
                //                                        }
                //                                    }
                //                                    _logger.Info("");
                //                                    break;
                //                                case "ShimDataBlock":
                //                                    var sdb = extraDataBase as ShimDataBlock;
                //                                    _logger.Warn(">> Shimcache data block");
                //                                    _logger.Info($"   LayerName: {sdb.LayerName}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "SpecialFolderDataBlock":
                //                                    var sfdb = extraDataBase as SpecialFolderDataBlock;
                //                                    _logger.Warn(">> Special folder data block");
                //                                    _logger.Info($"   Special Folder ID: {sfdb.SpecialFolderId}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "TrackerDataBaseBlock":
                //                                    var tdb = extraDataBase as TrackerDataBaseBlock;
                //                                    _logger.Warn(">> Tracker database block");
                //                                    _logger.Info($"   Machine ID: {tdb.MachineId}");
                //                                    _logger.Info($"   MAC Address: {tdb.MacAddress}");
                //                                    _logger.Info($"   MAC Vendor: {GetVendorFromMac(tdb.MacAddress)}");
                //                                    _logger.Info($"   Creation: {tdb.CreationTime}");
                //                                    _logger.Info("");
                //                                    _logger.Info($"   Volume Droid: {tdb.VolumeDroid}");
                //                                    _logger.Info($"   Volume Droid Birth: {tdb.VolumeDroidBirth}");
                //                                    _logger.Info($"   File Droid: {tdb.FileDroid}");
                //                                    _logger.Info($"   File Droid birth: {tdb.FileDroidBirth}");
                //                                    _logger.Info("");
                //                                    break;
                //                                case "VistaAndAboveIDListDataBlock":
                //                                    var vdb = extraDataBase as VistaAndAboveIdListDataBlock;
                //                                    _logger.Warn(">> Vista and above ID List data block");
                //
                //                                    foreach (var shellBag in vdb.TargetIDs)
                //                                    {
                //                                        var val = shellBag.Value.IsNullOrEmpty() ? "(None)" : shellBag.Value;
                //                                        _logger.Info($"   {shellBag.FriendlyName} ==> {val}");
                //                                    }
                //
                //                                    _logger.Info("");
                //                                    break;
                //                            }
                //                        }
                //                    }
                //                }
                //
                //                sw.Stop();
                //
                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Info("");
                //                }
                //
                //                _logger.Info(
                //                    $"---------- Processed '{autoDest.SourceFile}' in {sw.Elapsed.TotalSeconds:N8} seconds ----------");
                //
                //                if (_fluentCommandLineParser.Object.Quiet == false)
                //                {
                //                    _logger.Info("\r\n");
                //                }

                return customDest;
            }

            catch (Exception ex)
            {
                _failedFiles.Add($"{jlFile} ==> ({ex.Message})");
                _logger.Fatal($"Error opening '{jlFile}'. Message: {ex.Message}");
                _logger.Info("");
            }

            return null;
        }

        private static string GetVendorFromMac(string macAddress)
        {
            //00-00-00	XEROX CORPORATION
            //"00:14:22:0d:94:04"

            var mac = string.Join("-", macAddress.Split(':').Take(3)).ToUpperInvariant();
            // .Replace(":", "-").ToUpper();

            var vendor = "(Unknown vendor)";

            if (_macList.ContainsKey(mac))
            {
                vendor = _macList[mac];
            }

            return vendor;
        }

        private static void SetupNLog()
        {
            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }
    }

    public sealed class AutoCsvOut
    {
        //

        //lnk file info
        public string SourceFile { get; set; }
        public string SourceCreated { get; set; }
        public string SourceModified { get; set; }
        public string SourceAccessed { get; set; }
        public string TargetCreated { get; set; }
        public string TargetModified { get; set; }
        public string TargetAccessed { get; set; }
        public int FileSize { get; set; }
        public string RelativePath { get; set; }
        public string WorkingDirectory { get; set; }
        public string FileAttributes { get; set; }
        public string HeaderFlags { get; set; }
        public string DriveType { get; set; }
        public string DriveSerialNumber { get; set; }
        public string DriveLabel { get; set; }
        public string LocalPath { get; set; }
        public string CommonPath { get; set; }
        public string TargetIDAbsolutePath { get; set; }
        public string TargetMFTEntryNumber { get; set; }
        public string TargetMFTSequenceNumber { get; set; }
        public string MachineID { get; set; }
        public string MachineMACAddress { get; set; }

        public string TrackerCreatedOn { get; set; }
        public string ExtraBlocksPresent { get; set; }
    }

    public sealed class CustomCsvOut
    {
        //

        //lnk file info
        public string SourceFile { get; set; }
        public string SourceCreated { get; set; }
        public string SourceModified { get; set; }
        public string SourceAccessed { get; set; }
        public string TargetCreated { get; set; }
        public string TargetModified { get; set; }
        public string TargetAccessed { get; set; }
        public int FileSize { get; set; }
        public string RelativePath { get; set; }
        public string WorkingDirectory { get; set; }
        public string FileAttributes { get; set; }
        public string HeaderFlags { get; set; }
        public string DriveType { get; set; }
        public string DriveSerialNumber { get; set; }
        public string DriveLabel { get; set; }
        public string LocalPath { get; set; }
        public string CommonPath { get; set; }
        public string TargetIDAbsolutePath { get; set; }
        public string TargetMFTEntryNumber { get; set; }
        public string TargetMFTSequenceNumber { get; set; }
        public string MachineID { get; set; }
        public string MachineMACAddress { get; set; }

        public string TrackerCreatedOn { get; set; }
        public string ExtraBlocksPresent { get; set; }
    }

    internal class ApplicationArguments
    {
        public string File { get; set; }
        public string Directory { get; set; }

        public string JsonDirectory { get; set; }
        public bool JsonPretty { get; set; }
        public bool AllFiles { get; set; }

        public string LnkDumpDirectory { get; set; }

        public bool IncludeLnkDetail { get; set; }

        public string CsvFile { get; set; }
        public string XmlDirectory { get; set; }
        public string xHtmlDirectory { get; set; }

        public bool Quiet { get; set; }

        //  public bool LocalTime { get; set; }
    }
}