﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using QuantConnect.ToolBox;
using Newtonsoft.Json;
using Ionic.Zip;
using System.Threading;

namespace QuantConnect.Bovespa
{
    class Program
    {
        private static string _menu;

        private const string ConfigFileName = "config.json";
        private const string InstrumentsFileName = "instruments.txt";
        private const string TickerChangeFileName = "tickerchange.txt";
        
        private static ConfigSettings _settings;
        private static Dictionary<string, LeanInstrument> _instruments;

        static readonly CultureInfo _ptBR = CultureInfo.CreateSpecificCulture("pt-BR");
        static readonly CultureInfo _enUS = CultureInfo.CreateSpecificCulture("en-US");
        
        /// <summary>
        /// Primary entry point to the program
        /// </summary>
        static void Main(string[] args)
        {
            //Document the process:
            Console.WriteLine("QuantConnect.ToolBox: Bovespa Converter: ");
            Console.WriteLine("=========================================");
            Console.WriteLine("The Bovespa converter transforms Bovespa data into\r\n   the LEAN Algorithmic Trading Engine Data Format.");
            Console.WriteLine("Settings are loaded from the config.json file.");
            Console.WriteLine();
            
            // Load instrument list
            if (!LoadInstruments()) { Console.ReadKey(); Environment.Exit(0); };

            // Read configuration 
            if (!LoadConfiguration()) { Console.ReadKey(); Environment.Exit(0); };

            _menu =
                string.Format("0. Check Bovespa FTP site for new files (compares {0} content).\n", _settings.InputFolder) +
                string.Format("1. Convert Bovespa {0} data.\n", _settings.InputDataType) +
                "2. Create LEAN map files.\n" +
                "3. Create LEAN factor files.\n" +
                "4. Create LEAN holiday file.\n" +
                //string.Format("5. Create LEAN {0}-resolution files with {1} data.\n", _settings.OutputResolution, _settings.InputDataType) +
                //string.Format("6. Write custom {0}-resolution files with {1} data.\n", _settings.OutputResolution, _settings.InputDataType) +
                "q. Quit\n" +
                ">> Insira opção: ";
            Console.Write(_menu);

            var key = string.Empty;

            do
            {
                key = Console.ReadLine().ToLower().Trim();
                
                switch (key.Split(' ')[0])
                {
                    case "0":
                        CheckForNewRawFiles();
                        break;
                    case "1":
                        BovespaConverter();
                        break;
                    case "2":
                        CreateMapFiles();
                        break;
                    case "3":
                        CreateFactorFiles();
                        break;
                    case "4":
                        CreateHolidayFile();
                        break;
                    case "5":
                        Tick2Bar();
                        break;
                    case "6":
                        Convert2CustomCSV();
                        break;
                    case "show":
                        Process.Start(new ProcessStartInfo { FileName = Environment.CurrentDirectory, UseShellExecute = true });
                        break;
                    default:
                        Console.WriteLine("\nInvalid option!\n" + _menu);
                        break;
                }
            } while (key != "q");
        }

        /// <summary>
        /// Loads the instrument list from the instruments.txt file
        /// </summary>
        /// <returns></returns>
        private static bool LoadInstruments()
        {
            _instruments = new Dictionary<string, LeanInstrument>();
            
            if (!File.Exists(InstrumentsFileName))
            {
                Console.WriteLine(InstrumentsFileName + " file not found.");
                return false;
            }

            var lines = File.ReadAllLines(InstrumentsFileName);
            foreach (var line in lines)
            {
                var tokens = line.Split(',');
                if (tokens.Length >= 3)
                {
                    _instruments.Add(tokens[0], new LeanInstrument
                    {
                        Symbol = tokens[0],
                        Name = tokens[1],
                        Type = (InstrumentType)Enum.Parse(typeof(InstrumentType), tokens[2])
                    });
                }
            }

            return true;
        }

        /// <summary>
        /// Loads configuration settings from the config.json file
        /// </summary>
        /// <returns></returns>
        private static bool LoadConfiguration()
        {
            if (!File.Exists(ConfigFileName))
            {
                Console.WriteLine(ConfigFileName + " file not found.");
                return false;
            }

            _settings = JsonConvert.DeserializeObject<ConfigSettings>(File.ReadAllText(ConfigFileName));

            return ValidateConfiguration();
        }

        /// <summary>
        /// Validates the loaded configuration settings
        /// </summary>
        /// <returns></returns>
        private static bool ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_settings.InputFolder))
            {
                Console.WriteLine("Bovespa input folder is required.");
                return false;
            }

            if (!Directory.Exists(_settings.OutputFolder))
            {
                Console.WriteLine("Bovespa input folder does not exist.");
                return false;
            }
            
            if (string.IsNullOrWhiteSpace(_settings.OutputFolder))
            {
                Console.WriteLine("The Lean data folder is required.");
                return false;
            }
            
            if (!Directory.Exists(_settings.OutputFolder))
                Directory.CreateDirectory(_settings.OutputFolder);

            foreach (var symbol in _settings.InstrumentList)
            {
                if (!_instruments.ContainsKey(symbol))
                {
                    Console.WriteLine("Invalid symbol requested: {0}", symbol);
                    return false;
                }
            }

            if (_settings.InputDataType != "daily" && _settings.InputDataType != "trade" && _settings.InputDataType != "ask" && _settings.InputDataType != "bid")
            {
                Console.WriteLine("Invalid input data type requested: {0}. Check config.json and change to valid one.", _settings.InputDataType);
                return false;
            }

            if (_settings.OutputResolution != "daily" && _settings.OutputResolution != "hour" &&
                _settings.OutputResolution != "minute" && _settings.OutputResolution != "second")
            {
                Console.WriteLine("Invalid resolution requested: {0}. Check config.json and change to valid one.", _settings.OutputResolution);
                return false;
            }
            
            if (_settings.EndDate < _settings.StartDate)
            {
                Console.WriteLine("The end date must be greater or equal to the start date.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks for new Bovespa trade/bid/ask files available for downloading
        /// </summary>
        /// <returns></returns>
        private static async Task CheckForNewRawFiles()
        {
            var localFiles = new DirectoryInfo(_settings.InputFolder).GetDirectories()
                .ToDictionary(x => x.Name, y => y.GetFiles("*.zip").Select(z => z.Name.ToUpper()).ToList());

            var serverUris = new Dictionary<string, string>()
            {
                { "equity",  "ftp://ftp.bmf.com.br/MarketData/Bovespa-Vista/"},
                { "options", "ftp://ftp.bmf.com.br/MarketData/Bovespa-Opcoes/"},
                { "futures", "ftp://ftp.bmf.com.br/MarketData/BMF/"}
            };

            foreach (var serverUri in serverUris)
            {
                try
                {
                    Console.Write(serverUri.Value);
                    var request = WebRequest.Create(serverUri.Value);
                    request.Proxy = null;
                    request.Credentials = new NetworkCredential("anonymous", "me@home.com");
                    request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;

                    using (var resp = (FtpWebResponse)request.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.ASCII))
                    {
                        var data = sr.ReadToEnd().Split('\n').Where(d => d.Contains(".zip") && !d.Contains("FRAC"))
                            .Select(d => { var o = d.Split(' '); return o.Last().Replace("\r", "").Trim().ToUpper(); })
                            .Except(localFiles[serverUri.Key]).ToList();

                        Console.WriteLine("\t{0} file(s) to download.", data.Count);
                        data.ForEach(d => Console.WriteLine(d));

                        if (data.Count > 0) Process.Start("chrome.exe", serverUri.Value);
                    }
                }
                catch (Exception e) { Console.WriteLine(e.Message); }
            }
            Console.WriteLine("... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Chooses the converter based on input-data-type (config.json).
        /// </summary>
        /// <returns></returns>
        private static async Task BovespaConverter()
        {
            if (_settings.OutputResolution == "daily")
                await DailyFilesConverter();
            else
                await IntradayFilesConverter();
        }

        /// <summary>
        /// Converts Bovespa zip files with daily data for equities into Lean format (input-data-type = "daily")
        /// </summary>
        /// <returns></returns>
        private static async Task DailyFilesConverter()
        {
            var zipfiles = new DirectoryInfo(_settings.InputFolder + @"\equity").GetFiles("COTAHIST_A*zip")
                .Where(f => f.Name.Substring(f.Name.Length - 8, 4).ToInt32() > 1997).ToList();

            if(zipfiles.Count==0) 
            {
                Console.WriteLine("No zip files with daily Bovespa data.");
                return;
            }

            var sum = 0.0;
            var total = (long)zipfiles.Count;

            total = zipfiles.Sum(z => z.Length);
            
            //FolderCleanUp("equity", "daily");

            var outputdir = Directory.CreateDirectory(_settings.OutputFolder + @"\equity\bra\daily\");

            var starttime = DateTime.Now;

            Console.WriteLine(zipfiles.Count + " zip files with Bovespa daily data (" + (total / Math.Pow(1024, 2)).ToString("0.00 MB") + ")   \t" + starttime);
            
            foreach (var zipfile in zipfiles)
            {
                starttime = DateTime.Now;
                var data = await ReadAsyncZipFile(zipfile);
                data.RemoveAll(d => Filter(d));

                data.GroupBy(d => d.Substring(12, 12).Trim().ToLower() + ".csv").ToList().ForEach(d =>
                {
                    File.AppendAllLines(outputdir.FullName + d.Key, d.Select(l =>
                    {
                        return l.Substring(2, 8) + "," +
                            100 * Convert.ToInt64(l.Substring(56, 13)) + "," +
                            100 * Convert.ToInt64(l.Substring(69, 13)) + "," +
                            100 * Convert.ToInt64(l.Substring(82, 13)) + "," +
                            100 * Convert.ToInt64(l.Substring(108, 13)) + "," +
                            100 * Convert.ToInt64(l.Substring(152, 18));
                    }).OrderBy(l => l));
                });
                Console.WriteLine((sum += zipfile.Length / total).ToString("0.00%\t") + zipfile.Name.ToUpper() +
                    " read in " + (DateTime.Now - starttime).ToString(@"ss\.ff") + " secs");
            }

            var csvFiles = outputdir.GetFiles("*.csv").ToDictionary(x => x.Name.Replace(".csv", "").Trim(), y => y);
            Console.WriteLine(csvFiles.Count + " csv files to zip.\t" + DateTime.Now);

            sum = 0.0;
            total = csvFiles.Values.Sum(c => c.Length);
            
            foreach(var kvp in csvFiles)
            {
                await Task.Factory.StartNew(() => Compression.Zip(kvp.Value.FullName));
                Console.Write("\r" + (sum += kvp.Value.Length / total).ToString("0.00%\t") + kvp.Key.ToUpper() + "\t");
            };
            Console.WriteLine("\r... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Converts Bovespa zip files with trade/bid/ask data for equities, options and futures into Lean format
        /// </summary>
        /// <returns></returns>
        private static async Task IntradayFilesConverter()
        {
            var zipfiles = new DirectoryInfo(_settings.InputFolder + @"\" + _settings.SecurityType)
                .GetFiles((_settings.InputDataType == "bid" ? "OFER_CPA" : _settings.InputDataType == "ask" ? "OFER_VDA" : "NEG") + "*zip")
                .Where(f => f.Name.Substring(f.Name.Length - 12, 8).ToDateTime() > _settings.StartDate).ToList();
            
            if (zipfiles.Count == 0)
            {
                Console.WriteLine("No zip files with {0} Bovespa data.", _settings.InputDataType);
                return;
            }

            ZipFile zip;
            var sum = 0.0;
            var total = zipfiles.Sum(z => z.Length);
            var destinationFolder = Directory.CreateDirectory(_settings.OutputFolder + @"\" + _settings.SecurityType + @"\bra\" +
                (_settings.InputDataType == "trade" ? "tick" : _settings.InputDataType) + @"\");

            // Delete existing files
            var csvFiles = destinationFolder.GetFiles("*_" + _settings.InputDataType + "_Tick.csv").ToList();
            csvFiles.ForEach(c => c.Delete());
            
            // 
            var starttime = DateTime.Now;
            Console.WriteLine(zipfiles.Count + " zip files with Bovespa {0} data (" + (total / Math.Pow(1024, 3)).ToString("0.00 GB") + ")   \t" + starttime, _settings.InputDataType);

            foreach (var zipfile in zipfiles)
            {
                Compression.Unzip(zipfile.FullName, out zip);
                
                for (var i = 0; i < zip.Entries.Count; i++)
                {
                    using (var reader = new StreamReader(zip[i].OpenReader()))
                    {
                        while (!reader.EndOfStream)
                        {
                            var csv = (await reader.ReadLineAsync()).Split(';');

                            if (csv.Length < 5) continue;

                            // Ticker
                            if (!ValidateSymbol(csv[1] = csv[1].Trim())) continue;
                            
                            // TimeOfDay
                            csv[0] =  csv[0].Replace("-", "");

                            // Data
                            csv[2] = TimeSpan.Parse(csv[5]).TotalMilliseconds.ToString("F0") + "," +
                                (10000 * decimal.Parse(csv[3])).ToString("F0") + "," + csv[4].ToInt64() + "\r\n";

                            // CSV file
                            csv[6] = destinationFolder.FullName + @"\" + csv[0] + "_" + csv[1].ToLower() + "_Trade_Tick.csv";
                            
                            File.AppendAllText(csv[6], csv[2]);
                        }
                    }
                }
                Console.Write("\r" + (sum += zipfile.Length / total).ToString("0.00%") + "\t" + (DateTime.Now - starttime).ToString(@"hh\:mm\:ss") +
                    "   Last file: " + zipfile.Name.ToUpper());
            }

            var subdirs = new Dictionary<string, string>();
            csvFiles = destinationFolder.GetFiles("*_" + _settings.InputDataType + "_Tick.csv").ToList();

            sum = 0.0;
            total = csvFiles.Sum(z => z.Length);

            starttime = DateTime.Now;
            Console.WriteLine(csvFiles.Count + " csv files with {0} data (" + (total / Math.Pow(1024, 3)).ToString("0.00 GB") + ") to zip.   \t" + starttime, _settings.InputDataType);

            foreach (var csvFile in csvFiles)
            {
                var data = csvFile.Name.Split('_');
                
                var date = data[0];
                var ticker = data[1];
                
                if(!subdirs.ContainsKey(ticker))
                {
                    var subdir = Directory.CreateDirectory(destinationFolder.FullName + @"\" + ticker + @"\");
                    subdirs.Add(ticker, subdir.FullName);
                }

                Compression.Zip(subdirs[ticker] + data[0] + "_trade.zip",
                    new Dictionary<string, string> { { csvFile.Name, File.ReadAllText(csvFile.FullName) } });

                Console.Write("\r" + (sum += csvFile.Length / total).ToString("0.00%") + "\t" + (DateTime.Now - starttime).ToString(@"hh\:mm\:ss") +
                    "   Last file: " + csvFile.Name.ToUpper());

                csvFile.Delete();
            }
            Console.WriteLine("\r... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Creates map files to track ticker changes
        /// </summary>
        /// <returns></returns>
        private static void CreateMapFiles()
        {
            if (_settings.SecurityType == "futures")
            {
                CreateMapFilesFutures();
                return;
            }

            var dailyfolder = new DirectoryInfo(_settings.OutputFolder + @"\equity\bra\daily\");
            var zipfiles = dailyfolder.GetFiles("*.zip").ToList();

            if (zipfiles.Count == 0)
            {
                Console.WriteLine("No LEAN daily files. Convert Bovespa daily files first.");
                return;
            }

            var mapsDirectory = Directory.CreateDirectory(_settings.OutputFolder + @"\equity\bra\map_files\").FullName;
            new DirectoryInfo(mapsDirectory).GetFiles().ToList().ForEach(f => f.Delete());
            
            var tickerChangeDic = File.ReadAllLines(TickerChangeFileName).Select(t => t.Split(',')).ToDictionary(
                x => dailyfolder + x[0].Trim().ToLower() + ".zip",
                y =>
                {
                    var list = new string[y.Length - 1];
                    for (var i = 1; i < y.Length; i++)
                        if(!y[i].Contains("//")) list[i - 1] = dailyfolder + y[i].Trim().ToLower() + ".zip";
                    return list.Where(l => l != null).ToArray();
                });

            var oldtickers = new List<string>();

            ZipFile zip;
            ZipFile old;

            var sum = 0.0;
            var total = zipfiles.Sum(z => z.Length);

            foreach (var zipfile in zipfiles)
            {
                using (var newticker = Compression.Unzip(zipfile.FullName, out zip))
                {
                    var ticker = zipfile.Name.Replace(".zip", "");

                    if (tickerChangeDic.ContainsKey(zipfile.FullName))
                    {
                        foreach (var filename in tickerChangeDic[zipfile.FullName])
                        {
                            using (var oldticker = Compression.Unzip(filename, out old))
                                File.AppendAllText(mapsDirectory + ticker + ".csv",
                                    oldticker.ReadToEnd().Split('\n').First().Substring(0, 8) + "," +
                                    old.Entries.First().FileName.Replace(".csv", "\r\n"));

                            oldtickers.Add(mapsDirectory + old.Entries.First().FileName);
                        }                       
                    }

                    var data = newticker.ReadToEnd().Split('\n').ToList();
                    data.RemoveAll(d => string.IsNullOrWhiteSpace(d));
                    var end = (DateTime.Now - data.Last().ToDateTime()) > new TimeSpan(365, 0, 0, 0) ? data.Last() : "20251231";

                    File.AppendAllText(mapsDirectory + ticker + ".csv",
                        data.First().Substring(0, 8) + "," + ticker + "\r\n" + end.Substring(0, 8) + "," + ticker);

                    Console.Write("\r" + (sum += zipfile.Length / total).ToString("0.00%\t") + ticker.ToUpper() + "\t");
                }
            }

            oldtickers.ForEach(ot => File.Delete(ot));

            new DirectoryInfo(mapsDirectory).GetFiles().ToList()
                .ForEach(f => File.WriteAllLines(f.FullName, File.ReadAllLines(f.FullName).OrderBy(l => l)));

            Console.WriteLine("\r... exiting routine at " + DateTime.Now);
        }
        
        /// <summary>
        /// Creates map files to track ticker changes for futures
        /// </summary>
        /// <returns></returns>
        private static void CreateMapFilesFutures()
        {
            throw new NotImplementedException();
        
            var mm = "0fghjkmnquvxz".ToList();
            var tradingdays = TradingDays().Result.Select(t => t.ToDateTime()).ToList();
            var mapdir = Directory.CreateDirectory(@"map_files\");

            #region Expiration dates for index futures
            var winfut = new FileInfo(mapdir.FullName + "winfut.csv");
            if (winfut.Exists) winfut.Delete();

            var indfut = new FileInfo(mapdir.FullName + "indfut.csv");
            if (indfut.Exists) indfut.Delete();

            tradingdays.FindAll(t => t.Year >= 2005 && t.Month % 2 == 0 && t.Day > 11 && t.Day < 19)
                .GroupBy(t => t.AddDays(1 - t.Day)).ToList()
                .ForEach(t =>
                {
                    var end = mm[t.Key.Month] + (t.Key.Year - 2000).ToString("00") + "\r\n";
                    var days = new List<DateTime>();

                    foreach (var i in new int[] { 3, 4, 5, 1, 2 })
                    {
                        days = t.Where(d => (int)d.DayOfWeek == i).ToList();
                        if (days.Count == 1) break;
                    }

                    var exday = tradingdays[tradingdays.IndexOf(days.First()) - 1].ToString("yyyyMMdd");
                    File.AppendAllText(winfut.FullName, exday + ",win" + end);
                    File.AppendAllText(indfut.FullName, exday + ",ind" + end);
                });
            #endregion

            #region Expiration dates for dolar futures
            var wdofut = new FileInfo(mapdir.FullName + "wdofut.csv");
            if (winfut.Exists) winfut.Delete();

            var dolfut = new FileInfo(mapdir.FullName + "dolfut.csv");
            if (indfut.Exists) indfut.Delete();

            tradingdays.FindAll(t => t.Year >= 2005 && t.Day > 11 && t.Day < 19)
                .GroupBy(t => t.AddDays(1 - t.Day)).ToList()
                .ForEach(t =>
                {
                    var end = mm[t.Key.Month] + (t.Key.Year - 2000).ToString("00") + "\r\n";
                    var days = new List<DateTime>();

                    foreach (var i in new int[] { 3, 4, 5, 1, 2 })
                    {
                        days = t.Where(d => (int)d.DayOfWeek == i).ToList();
                        if (days.Count == 1) break;
                    }

                    var exday = tradingdays[tradingdays.IndexOf(days.First()) - 1].ToString("yyyyMMdd");
                    File.AppendAllText(wdofut.FullName, exday + ",wdo" + end);
                    File.AppendAllText(dolfut.FullName, exday + ",dol" + end);
                });
            #endregion
        }

        /// <summary>
        /// Creates factor files with dividend and split/inplit ratios
        /// </summary>
        /// <returns></returns>
        private static async Task CreateFactorFiles()
        {
            if (_settings.SecurityType == "futures")
            {
                CreateFactorFilesFutures();
                return;
            }

            var errorFile = new FileInfo("error.txt");
            var codesFile = new FileInfo("codes.txt");

            var mapDirectory = Directory.CreateDirectory(_settings.OutputFolder + @"\equity\bra\map_files\");
            var mapFiles = mapDirectory.GetFiles("*.csv").ToList();

            if (_settings.InstrumentList.Length > 0) mapFiles.RemoveAll(m => !_settings.InstrumentList.Contains(m.Name.Replace(".csv", "").ToUpper()));

            if (mapFiles.Count == 0)
            {
                Console.WriteLine("No LEAN map files. Create map files first.");
                return;
            }

            // Get tickers that can be processed
            var tickers = new List<string>();

            foreach (var mapFile in mapFiles) tickers.AddRange(File.ReadAllLines(mapFile.FullName).Select(x => x.Substring(9)));
            tickers = tickers.Distinct().ToList();

            // Prepare factor files
            var factorsDirectory = Directory.CreateDirectory(mapDirectory.FullName.Replace("map_", "factor_")).FullName;
            var factorFiles = new DirectoryInfo(factorsDirectory).GetFiles().ToDictionary(x => x.Name.Replace(".csv", ""), y => y.FullName);
            foreach (var ticker in tickers) if (factorFiles.ContainsKey(ticker)) File.Delete(factorFiles[ticker]);
            
            // Get CVM codes (we need this to access the equity factor data)
            var codes = await GetCodes("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", errorFile.FullName);

            if (errorFile.Exists)
            {
                var errorGetCodes = File.ReadAllLines(errorFile.FullName).Where(l => l.Contains("GetCodes")).ToList();
                foreach (var errorGetCode in errorGetCodes) Console.WriteLine(errorGetCode);
            }

            //
            var starttime = DateTime.Now;
            var kind = new Dictionary<int, string> { { 3, "ON" }, { 4, "PN" }, { 5, "PNA" }, { 6, "PNB" }, { 7, "PNC" }, { 8, "PND" }, { 11, "UNT" } };

            // 
            foreach (var code in codes)
            {
                var index = 0;

                var page0 = await DownloadAsync("http://www.bmfbovespa.com.br/pt-br/mercados/acoes/empresas/ExecutaAcaoConsultaInfoEmp.asp?CodCVM=" + code);
                
                if ((index = page0.IndexOf("Papel=") + 6) < 6) continue;

                page0 = page0.Substring(index, 4).ToLower();

                var thiscodesymbols = tickers.FindAll(t => t.Contains(page0));
                if (thiscodesymbols.Count == 0) continue;
  
                var page1 = await DownloadAsync("http://www.bmfbovespa.com.br/Cias-Listadas/Empresas-Listadas/ResumoProventosDinheiro.aspx?codigoCvm=" + code);
                var page2 = await DownloadAsync("http://www.bmfbovespa.com.br/Cias-Listadas/Empresas-Listadas/ResumoEventosCorporativos.aspx?codigoCvm=" + code);

                if (!page1.Contains("Proventos em Dinheiro")) page1 = string.Empty;
                if ((index = page1.IndexOf("<tbody>")) >= 0) page1 = page1.Substring(0, page1.IndexOf("</tbody>")).Substring(index);

                if (!page2.Contains("Proventos em Ações")) page2 = string.Empty;
                if ((index = page2.IndexOf("<tbody>")) >= 0) page2 = page2.Substring(0, page2.IndexOf("</tbody>")).Substring(index);
                
                foreach (var symbol in thiscodesymbols)
                {
                    var date = new DateTime();
                    var keys = new List<DateTime>();
                    var fkeys = new List<DateTime>();

                    var events = new Dictionary<DateTime, decimal>();
                    var factors = new Dictionary<DateTime, decimal>();

                    var dividend = new Dictionary<DateTime, decimal>();
                    var comprice = new Dictionary<DateTime, decimal>();

                    #region Dividends
                    try
                    {
                        index = 0;

                        while (page1.Length > 0 && (index = page1.IndexOf(">" + kind[symbol.Substring(4).ToInt32()] + "<", index)) > 0)
                        {
                            index++;
                            var idx = 0;
                            var cols = new List<string>();
                            var row = page1.Substring(index, page1.IndexOf("</tr>", index) - index);

                            while ((idx = row.IndexOf("\">", idx) + 2) >= 2) cols.Add(row.Substring(idx, row.IndexOf("<", idx) - idx));

                            var currentcomprice = 0m;
                            if (!decimal.TryParse(cols[5], NumberStyles.Any, _ptBR, out currentcomprice) || currentcomprice <= 0) continue;

                            if (!DateTime.TryParseExact(cols[4], "dd/MM/yyyy", _ptBR, DateTimeStyles.None, out date) &&
                                !DateTime.TryParseExact(cols[3], "dd/MM/yyyy", _ptBR, DateTimeStyles.None, out date))
                                date = DateTime.ParseExact(cols[0], "dd/MM/yyyy", _ptBR, DateTimeStyles.None);
                 
                            if (!comprice.ContainsKey(date)) comprice.Add(date, currentcomprice);

                            if (dividend.ContainsKey(date))
                                dividend[date] += decimal.Parse(cols[1], _ptBR);
                            else
                                dividend.Add(date, decimal.Parse(cols[1], _ptBR));

                        }
                        events = comprice.OrderBy(x => x.Key).ToDictionary(x => x.Key, y => 1m);
                        comprice = comprice.OrderBy(x => x.Key).ToDictionary(x => x.Key, y => y.Value);
                        dividend = dividend.OrderBy(x => x.Key).ToDictionary(x => x.Key, y => y.Value);

                        fkeys = comprice.Keys.ToList();
                        for (var i = 0; i < fkeys.Count; i++)
                        {
                            var factor = 1 - dividend[fkeys[i]] / comprice[fkeys[i]];

                            for (var j = i + 1; j < fkeys.Count; j++)
                                factor *= (1 - dividend[fkeys[j]] / comprice[fkeys[j]]);

                            factors.Add(fkeys[i], factor);
                        }
                        factors.Add(new DateTime(2049, 12, 31), 1m);
                    }
                    catch (Exception e)
                    {
                        File.AppendAllText(errorFile.FullName, "Dividends," + code + "," + e.Message + "\r\n");
                    }
                    #endregion

                    #region Corporate events
                    try
                    {
                        index = 0;

                        while (page2.Length > 1 && (index = page2.IndexOf("<tr", index + 1)) > 1)
                        {
                            index++;
                            var idx = 0;
                            var cols = new List<string>();
                            var row = page2.Substring(index, page2.IndexOf("/tr", index) - index);
                            if (row.Contains("Cisão")) continue;

                            while ((idx = row.IndexOf("\">", idx) + 2) >= 2) cols.Add(row.Substring(idx, row.IndexOf("<", idx) - idx));

                            if (!DateTime.TryParseExact(cols[2], "dd/MM/yyyy", _ptBR, DateTimeStyles.None, out date))
                                date = DateTime.ParseExact(cols[1], "dd/MM/yyyy", _ptBR, DateTimeStyles.None);
            
                            cols = cols[4].Split('/').ToList();

                            var event0 = 0m;
                            if (!decimal.TryParse(cols[0], NumberStyles.Any, _ptBR, out event0) || event0 <= 0) continue;

                            if (cols.Count == 1) event0 = 1 / (1m + event0 / 100m);
                            if (cols.Count == 2)
                            {
                                event0 = event0 / decimal.Parse(cols[1], _ptBR);
                                if (code == 9512) event0 = .1m;
                            }

                            if (events.ContainsKey(date))
                                events[date] = event0;
                            else
                                events.Add(date, event0);
                        }
                        events.Add(new DateTime(2049, 12, 31), 1m);
                        events = events.OrderBy(x => x.Key).ToDictionary(x => x.Key, y => y.Value);
                    }
                    catch (Exception e)
                    {
                        File.AppendAllText(errorFile.FullName, "CorpEvents," + code + "," + e.Message + "\r\n");
                    }
                    #endregion

                    keys = events.Keys.ToList();
                    for (var i = 0; i < keys.Count; i++) for (var j = i + 1; j < keys.Count; j++) events[keys[i]] *= events[keys[j]];

                    keys.Except(fkeys).ToList().ForEach(k => { if (!factors.ContainsKey(k)) factors.Add(k, 0m); });

                    for (var i = 0; i < keys.Count - 1; i++) if (factors[keys[i]] == 0) factors[keys[i]] = factors[keys[i + 1]];
                    factors = factors.OrderBy(x => x.Key).ToDictionary(x => x.Key, y => y.Value);

                    // Write to file
                    if (!factorFiles.ContainsKey(symbol)) factorFiles.Add(symbol, factorsDirectory + symbol + ".csv");

                    foreach (var key in keys)
                        File.AppendAllText(factorFiles[symbol], key.ToString("yyyyMMdd") + "," +
                            Math.Round(factors[key], 9).ToString(_enUS) + "," + Math.Round(events[key], 9).ToString(_enUS) + "\r\n");
                    
                }
                // Write codes and respective symbols
                File.AppendAllText(codesFile.FullName, code.ToString("00000") + ";" + string.Join(";", thiscodesymbols) + "\r\n");

                Console.Write("\r" + ((1 + codes.IndexOf(code)) / (double)codes.Count).ToString("0.00%") + "\t" + page0.ToUpper() + "\t" + (starttime - DateTime.Now).ToString(@"mm\:ss"));
            }

            // ReWrite codes and respective symbols, ordering by ticker alphabetically
            File.WriteAllLines(codesFile.FullName, File.ReadAllLines(codesFile.FullName).Distinct().OrderBy(l => l.Split(';')[1]));

            Console.WriteLine("\r... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Creates factor files with dividend and split/inplit ratios for futures
        /// </summary>
        /// <returns></returns>
        private static void CreateFactorFilesFutures()
        {
            throw new NotImplementedException();
        
            var dirs = new DirectoryInfo(@"tick\").GetDirectories()
                .GroupBy(d => d.Name.Substring(0, 3) + "fut").ToDictionary(x => x.Key, y => y.ToList());

            var mapdir = new DirectoryInfo(@"map_files\");
            var mapfiles = mapdir.GetFiles().ToDictionary(x => x.Name.Substring(0, 6), y => y);

            var facdir = Directory.CreateDirectory(mapdir.FullName.Replace("map", "factor"));
            var facfiles = mapfiles.ToDictionary(x => x.Key, y => new FileInfo(y.Value.FullName.Replace("map", "factor")));
            foreach (var kvp in facfiles) { if (kvp.Value.Exists) kvp.Value.Delete(); }

            foreach (var kvp in dirs)
            {
                if (!mapfiles.ContainsKey(kvp.Key)) continue;
                var exdates = File.ReadAllLines(mapfiles[kvp.Key].FullName).ToDictionary(x => x.Substring(9), y => y.Substring(0, 8));

                for (var i = 1; i < exdates.Count; i++)
                {
                    var keyprev = exdates.Keys.ToArray()[i - 1];
                    var keycurr = exdates.Keys.ToArray()[i];
                    var dirprev = kvp.Value.Find(d => d.Name == keyprev);
                    var dircurr = kvp.Value.Find(d => d.Name == keycurr);
                    if (dirprev == null || dircurr == null) continue;

                    var fileprev = dirprev.FullName + @"\" + exdates[keyprev] + ".zip";
                    var filecurr = dircurr.FullName + @"\" + exdates[keyprev] + ".zip";
                }
            }
        }

        /// <summary>
        /// Get CVM (Brazilian SEC) codes for all equities 
        /// </summary>
        /// <param name="alphabet">Letters for searching</param>
        /// <param name="errorFile">File to store errors</param>
        /// <returns></returns>
        private static async Task<List<int>> GetCodes(string alphabet, string errorFile)
        {
            var codes = new List<int>();

            //
            var codesFile = new FileInfo("codes.txt");

            if (codesFile.Exists)
            {
                alphabet = string.Empty;
                codes = File.ReadAllLines(codesFile.FullName).Select(c => int.Parse(c.Split(';')[0])).ToList();
            }

            if(!string.IsNullOrWhiteSpace(alphabet)) Console.Write("Getting codes: ");

            foreach (var letter in alphabet)
            {
                try
                {
                    var page = await DownloadAsync("http://cvmweb.cvm.gov.br/SWB/Sistemas/SCW/CPublica/CiaAb/FormBuscaCiaAbOrdAlf.aspx?LetraInicial=" + letter);
                    Console.Write(letter);

                    foreach (var tag in new string[] { "Linkbutton5&", "Linkbutton6&" })
                    {
                        var id = 0;
                        while ((id = page.IndexOf(tag, id) + 29) > 29)
                        {
                            id++;
                            var code = 0;
                            if (!int.TryParse(page.Substring(id, 6), out code) &&
                                !int.TryParse(page.Substring(id, 5), out code) &&
                                !int.TryParse(page.Substring(id, 4), out code) &&
                                !int.TryParse(page.Substring(id, 3), out code))
                                code = int.Parse(page.Substring(id, 2));
                            
                            if (!codes.Contains(code)) codes.Add(code);
                        }
                    }
                }
                catch (Exception) { File.AppendAllText(errorFile, "GetCodes," + letter + "\r\n"); }
            }

            if (codes.Contains(23264)) codes.Add(18112);

            Console.WriteLine(".\tTotal codes: " + codes.Count);

            return codes.OrderBy(i => i).ToList();
        }

        /// <summary>
        /// Creates holidays-bra.csv file with holiday dates
        /// </summary>
        /// <returns></returns>
        private static async Task CreateHolidayFile()
        {
            #region Market Hours Database

            var mrkthoursstrg = "America/Sao_Paulo,bra,,equity,-,-,-,-,9.5,10,16.9166667,18,9.5,10,16.9166667,18,9.5,10,16.9166667,18,9.5,10,16.9166667,18,9.5,10,16.9166667,18,-,-,-,-";
            var mrkthoursfile = new FileInfo(_settings.OutputFolder + @"\market-hours\market-hours-database.csv");
            if (mrkthoursfile.Exists && !File.ReadAllLines(mrkthoursfile.FullName).Any(l => l.Contains(mrkthoursstrg.Substring(0, 24))))
                File.AppendAllText(mrkthoursfile.FullName, "\r\n" + mrkthoursstrg + "\r\n");
            
            #endregion
            
            var holidays = new List<DateTime>();

            var ofile = new FileInfo(_settings.OutputFolder + @"\market-hours\holidays-bra.csv");
            var output = ofile.Exists
                ? File.ReadAllLines(ofile.FullName).ToList()
                : (new string[] { "year, month, day" }).ToList();
            var lastdate = output.Count == 1
                ? new DateTime(1997, 12, 31)
                : DateTime.ParseExact(output.Last(), "yyyy, MM, dd", _ptBR);

            if (lastdate == new DateTime(DateTime.Now.Year, 12, 31)) return;

            #region Get Holidays from Bovespa page
            try
            {
                var i = 0;
                var id = 0;
                var date = new DateTime();
                var page = await DownloadAsync("http://www.bmfbovespa.com.br/pt-br/regulacao/calendario-do-mercado/calendario-do-mercado.aspx");
                page = page.Substring(0, page.IndexOf("linhaDivMais"));

                var months = new string[] { 
                        "Jan", "Fev", "Mar",
                        "Abr", "Mai", "Jun",
                        "Jul", "Ago", "Set",
                        "Out", "Nov", "Dez" }.ToList();

                while (i < 12)
                {
                    while (i < 12 && (id = page.IndexOf(">" + months[i + 0] + "<")) < 0) i++;
                    var start = id + 1;

                    while (i < 11 && (id = page.IndexOf(">" + months[i + 1] + "<")) < 0) i++;
                    var count = id - start;

                    months[i] = count > 0 ? page.Substring(start, count) : page.Substring(start);

                    id = 0;
                    while ((id = months[i].IndexOf("img/ic_", id) + 6) > 6)
                    {
                        id++;
                        if (DateTime.TryParseExact(months[i].Substring(id, 2) + months[i].Substring(0, 3) + DateTime.Now.Year.ToString(),
                            "ddMMMyyyy", _ptBR, DateTimeStyles.None, out date))
                            holidays.Add(date);
                    }
                    i++;
                }
            }
            catch (Exception e) { Console.WriteLine(e.Message); }
            #endregion

            while (lastdate < holidays.First()) holidays.Add(lastdate = lastdate.AddDays(1));
            holidays.RemoveAll(d => d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday);

            var zipfiles = new DirectoryInfo(_settings.InputFolder + @"\equity").GetFiles("COTAHIST_A*zip").Where(zf =>
            {
                var zfyear = zf.Name.Substring(zf.Name.Length - 8, 4).ToInt32();
                return zfyear >= holidays.Min().Year && zfyear < holidays.Max().Year;
            }).ToArray();

            foreach (var zf in zipfiles)
            {
                var data = (await ReadAsyncZipFile(zf)).Select(d => d.Substring(2, 8)).Distinct().ToList();

                data.RemoveAt(0);   // Remove header  

                data.ForEach(d => holidays.Remove(d.ToDateTime()));
                Console.Write("\r" + zf.Name + "\t" + holidays.Count);
            }

            if (holidays.Count == 0)
            {
                Console.WriteLine("\rNo data to write into " + ofile.Name);
                return;
            }

            output.AddRange(holidays.OrderBy(d => d).Select(d => d.ToString("yyyy, MM, dd")));
            File.WriteAllText(ofile.FullName, string.Join("\r\n", output.ToArray()));
            Console.WriteLine("\r" + ofile.FullName + " written!");
        }

        /// <summary>
        /// Validades ticker name to avoid undesirable data
        /// </summary>
        /// <param name="ticker">ticker name to validate</param>
        /// <returns></returns>
        private static bool ValidateSymbol(string ticker)
        {
            if (ticker.Length < 5 || ticker.Length > 7) return false;

            int type;

            if (int.TryParse(ticker.Substring(4), out type))
                if (type < 3 || (type > 8 && type != 11)) return false;
               
            if (_settings.InstrumentList.Count() == 0) return true;

            if (_settings.SecurityType == "equity")
                return _settings.InstrumentList.Contains(ticker);

            if (_settings.SecurityType == "options")
                return _settings.InstrumentList.Contains(ticker.Substring(0, 4));

            if (_settings.SecurityType == "futures")
                return _settings.InstrumentList.Contains(ticker.Substring(0, 3));

            return false;
        }

        private static async Task Tick2Bar()
        {
            throw new NotImplementedException();
        
            string submenu;
            double periodDbl;
            
            var periodDic = new Dictionary<string, double> { { "hour", 36e4 }, { "minute", 6e4 }, { "second", 1e3 } };
            
            Console.WriteLine(submenu = "\rChoose period: hour, minute or second. exit to return to Main.");

            var period = Console.ReadLine().ToLower();

            while (!periodDic.TryGetValue(period, out periodDbl))
            {
                if (period == "exit") { Console.WriteLine(_menu); return; }
                Console.WriteLine("Invalid period: " + period + "\r\n" + submenu);
                period = Console.ReadLine().ToLower();
            }

            var dirs = new DirectoryInfo(_settings.InputFolder + @"\"+ _settings.SecurityType + @"\bra\tick\").GetDirectories().ToList();
            
            var sum = 0.0;
            var total = 0.0;
            dirs.ForEach(d => { total += d.GetFiles().Sum(f => f.Length); Console.Write("\r" + d.Name.ToUpper() + " " + (total / 1024 / 1024).ToString("0.00")); });
            Console.WriteLine("\r\n" + dirs.Count + " symbol directories to read.\t" + DateTime.Now);

            foreach (var dir in dirs)
            {
                var starttime = DateTime.Now;
                var outdir = Directory.CreateDirectory(dir.FullName.Replace("tick", period));
                var zipfiles = dir.GetFiles("*.zip").ToList();
                
                foreach (var zipfile in zipfiles)
                {
                    var output = new List<string>();
                    var data = await ReadAsyncZipFile(zipfile);

                    var ticks = double.Parse(data.First().Split(',')[0]);
                    ticks = ticks - ticks % 36e6 + 7.5 * 36e5;

                    // Group by period
                    data.GroupBy(d =>
                        {
                            var totalseconds = Math.Min(ticks, double.Parse(d.Split(',')[0]));
                            return (totalseconds - (totalseconds % periodDbl)).ToString();
                        })
                        // For each period, define bar and save
                        .ToList().ForEach(t =>
                        {
                            var price = t.Select(l => l.Split(',')[1].ToDecimal()).ToList();
                            var qunty = t.Select(l => l.Split(',')[2].ToDecimal()).ToList().Sum();
                            var volfin = t.Select(l => { var d = l.Split(','); return d[1].ToDecimal() * d[2].ToDecimal(); }).ToList().Sum() / 10000;
                            output.Add(t.Key + "," + price.First() + "," + price.Max() + "," + price.Min() + "," + price.Last() + "," + qunty + "," + volfin);
                        });

                    await Task.Factory.StartNew(() =>
                        {
                            var newFile = zipfile.FullName.Replace("tick", period);
                            var csvFile = new FileInfo(newFile.Replace("trade.zip", dir.Name + "_" + period + "_trade.csv"));

                            File.WriteAllLines(csvFile.FullName, output);

                            using (var z = new FileStream(newFile, FileMode.Create))
                            //using (var a = new ZipArchive(z, ZipArchiveMode.Create))
                            //    a.CreateEntryFromFile(csvFile.FullName, csvFile.Name, CompressionLevel.Optimal);

                            csvFile.Delete();
                        });
                    sum += zipfile.Length;
                }

                Console.Write("\r" + (sum / total).ToString("0.00%") + "\t" + dir.Name.ToUpper() + ": \t" + zipfiles.Count +
                    " days were read/written in " + (DateTime.Now - starttime).ToString(@"ss\.ff") + " secs.\t");
            }
            Console.WriteLine("\r\n... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Convert LEAN data to custom data
        /// </summary>
        /// <returns></returns>
        private static async Task Convert2CustomCSV()
        {
            if (_settings.OutputFormat == "lean") return;

            if (_settings.OutputResolution != "daily")
            {
                await Convert2CustomCSV(_settings.OutputResolution);
                return;
            }

            var zipfiles = new DirectoryInfo(_settings.OutputFolder + @"/" + _settings.SecurityType + @"/bra/daily/").GetFiles("*.zip").ToList();

            if (zipfiles.Count == 0)
            {

            }

            var sum = 0.0;
            var total = zipfiles.Sum(f => f.Length);
            var roottime = DateTime.Now;

            Console.WriteLine(zipfiles.Count + " symbol to read.(" + (total / 1024 / 1024).ToString("000.00") + "MB)\t" + roottime);

            foreach (var zipfile in zipfiles)
            {
                var starttime = DateTime.Now;
                var symbol = zipfile.Name.Replace(".zip", "");
                var csvFile = new FileInfo(symbol.ToUpper() + "_Diário.csv");
                if (csvFile.Exists) csvFile.Delete();

                var factors = GetTickerFactors(symbol);

                File.WriteAllLines(csvFile.FullName, (await ReadAsyncZipFile(zipfile)).Select(l =>
                {
                    var data = l.Split(',');
                    var date = data[0].ToDateTime();
                    var factor = factors.FirstOrDefault(kvp => kvp.Key >= date);

                    return symbol.ToUpper() + ";" + date.ToString("dd/MM/yyyy") + ";" +
                        Math.Round(data[1].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                        Math.Round(data[2].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                        Math.Round(data[3].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                        Math.Round(data[4].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                        (data.Length == 6 ? data[5] : data[6] + ";" + data[5]);

                }).ToArray());

                Console.Write("\r" + ((sum += zipfile.Length) / total).ToString("0.00%\t") + symbol.ToUpper() +
                    "\twas read/written in " + (DateTime.Now - starttime).ToString(@"ss\.ff") + " secs\t");
            }
            Console.WriteLine("\r... exiting routine at " + DateTime.Now + ". Took " + (DateTime.Now - roottime).ToString(@"mm\:ss"));
        }
        
        /// <summary>
        /// Convert LEAN data to custom data
        /// </summary>
        /// <param name="OutputResolution">Output resoltution for intraday cases (hour/minute/second)</param>
        /// <returns></returns>
        private static async Task Convert2CustomCSV(string OutputResolution)
        {
            var symbols = new DirectoryInfo(_settings.OutputFolder + @"/" + _settings.SecurityType + @"/bra/" + OutputResolution + @"/")
                .GetDirectories().ToList();

            if (_settings.InstrumentList.Length > 0) symbols.RemoveAll(s => !_settings.InstrumentList.Contains(s.Name.ToUpper()));

            if (symbols.Count == 0)
            {

            }

            var sum = 0.0;
            var total = 0.0;
            symbols.ForEach(d => total += d.GetFiles().Sum(f => f.Length));
            
            var roottime = DateTime.Now;
            Console.WriteLine("\r" + symbols.Count + " symbol directories to read (" + (total / 1024 / 1024).ToString("000.00") + " MB).\t" + roottime);

            foreach (var symbol in symbols)
            {
                var starttime = DateTime.Now;
                var factors = GetTickerFactors(symbol.Name);
                var zipfiles = symbol.GetFiles("*.zip").ToList();
                var csvFile = new FileInfo(symbol.Name.ToUpper() + "_" + _settings.OutputResolution + ".csv");
                if (csvFile.Exists) csvFile.Delete();

                foreach (var zipfile in zipfiles)
                {
                    var date = zipfile.Name.ToDateTime();
                    var factor = factors.FirstOrDefault(kvp => kvp.Key >= date);

                    File.AppendAllLines(csvFile.FullName, (await ReadAsyncZipFile(zipfile)).Select(l =>
                    {
                        var data = l.Split(',');

                        return symbol.Name.ToUpper() + ";" +
                            date.AddMilliseconds(data[0].ToInt64()).ToString(@"dd/MM/yyyy;HH\:mm\:ss") + ";" +
                            Math.Round(data[1].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                            Math.Round(data[2].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                            Math.Round(data[3].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                            Math.Round(data[4].ToDecimal() * factor.Value / 10000, 2).ToString("0.00", _ptBR) + ";" +
                            (data.Length == 6 ? data[5] : data[6] + ";" + data[5]);

                    }).ToArray());

                    sum += zipfile.Length;
                }

                Console.Write("\r" + (sum / total).ToString("0.00%") + "\t" + symbol.Name.ToUpper() + ": \t" + zipfiles.Count +
                " days were read/written in " + (DateTime.Now - starttime).ToString(@"ss\.ff") + " secs.\t");
            }

            // For options and futures
            var csvFiles = new DirectoryInfo(Environment.CurrentDirectory).GetFiles("*_" + _settings.OutputResolution + ".csv");

            if (csvFiles.Count() > 0)
            {
                sum = 0.0;
                total = csvFiles.Sum(f => f.Length);
                Console.WriteLine("Zipping " + (total / 1024 / 1024).ToString("000.00") + " MB");

                csvFiles.GroupBy(g =>
                {
                    if (_settings.SecurityType == "equity") return g.Name.Replace(".csv", ".zip");
                    if (_settings.SecurityType == "futures") return g.Name.Substring(0, 3).ToUpper() + "FUT" + "_" + _settings.OutputResolution + ".zip";

                    var type = ("ABCDEFGHIJKL".Contains(g.Name[4]) ? "_C" : "_P") + "_" + _settings.OutputResolution + ".zip";
                    return g.Name.Substring(0, 4) + type;
                })
                    //.ToList().ForEach(f =>
                    //{
                    //    var outputfile = new FileInfo(f.Key);
                    //    if (outputfile.Exists) outputfile.Delete();

                    //    Compression.Zip(outputfile.FullName);

                    //    using (var z = new FileStream(outputfile.FullName, FileMode.Create))

                    //    using (var a = new ZipArchive(z, ZipArchiveMode.Create, true))
                    //        f.ToList().ForEach(csvFile =>
                    //        {
                    //            a.CreateEntryFromFile(csvFile.FullName, csvFile.Name, CompressionLevel.Optimal);
                    //            csvFile.Delete();
                    //            sum += csvFile.Length;
                    //            Console.Write("\r" + (sum / total).ToString("0.00%") + "\tLast zippped file:\t" + csvFile.Name.ToUpper());
                    //        });
                    //})
                    ;
            }
            Console.WriteLine("\r\n... exiting routine at " + DateTime.Now);
        }

        /// <summary>
        /// Read factor files and return the date/value pair
        /// </summary>
        /// <param name="symbol">ticker</param>
        /// <returns></returns>
        private static SortedList<DateTime, decimal> GetTickerFactors(string symbol)
        {
            var factors = new SortedList<DateTime, decimal>();

            var file = new FileInfo(_settings.OutputFolder + @"\" + _settings.SecurityType + @"\bra\factor_files\" + symbol + ".csv");

            if (file.Exists)
            {
                File.ReadAllLines(file.FullName).ToList().ForEach(line =>
                {
                    var data = line.Split(',');
                    var factor = data[1].ToDecimal() * data[2].ToDecimal();
                    factors.Add(data[0].ToDateTime(), factor);
                });
            }
            else
                factors.Add(new DateTime(2049, 12, 31), 1m);
            
            return factors;
        }

        private static async Task SearchTickerChange()
        {
            var index = 0;
            var page = string.Empty;
            var cancel = new List<string>();
            var merged = new List<string>();

            #region Get Cancelled and Incorporated companies from BmfBovespa site
            try
            {
                var codes = new List<string>();

                page = await DownloadAsync("http://www.bmfbovespa.com.br/cias-Listadas/empresas-com-registro-cancelado/ResumoEmpresasComRegistroCancelado.aspx?razaoSocial=");
                if ((index = page.IndexOf("<tbody>")) >= 0) page = page.Substring(0, page.IndexOf("</tbody>")).Substring(index);

                index = 0;
                while (page.Length > 1 && (index = page.IndexOf("codigo=", index) + 7) > 7)
                {
                    var code = page.Substring(index, 4);
                    if (!codes.Contains(code)) codes.Add(code);
                }

                foreach (var code in codes)
                {
                    page = await DownloadAsync("http://www.bmfbovespa.com.br/cias-Listadas/empresas-com-registro-cancelado/DetalheEmpresasComRegistroCancelado.aspx?codigo=" + code);
                    if ((index = page.IndexOf("lblMotivo")) < 0) { Console.Write(" " + code); continue; }
                    page = page.Substring(0, page.IndexOf("</tbody>")).Substring(index);
                    if ((index = page.IndexOf(">")) > 0) page = code + page.Substring(0, page.IndexOf("<")).Substring(index);
                    if (page.ToLower().Contains("incorporad") || code == "SUBA") merged.Add(page); else cancel.Add(page);
                }
            }
            catch (Exception e) { Console.WriteLine("\r\n" + page.Substring(0, 20) + "\r\n" + e.Message); }
            #endregion
            File.WriteAllLines("Canceladas.txt", cancel.OrderBy(x => x));
            File.WriteAllLines("Incorporadas.txt", merged.OrderBy(x => x));
            cancel = cancel.Select(c => c.Substring(0, 4)).ToList();

            //TickerChange.Keys.ToList().ForEach(k => { if (!cancel.Contains(k.Substring(0, 4))) cancel.Add(k.Substring(0, 4)); });
            //TickerChange.Values.ToList().ForEach(k => { if (!cancel.Contains(k.Substring(0, 4))) cancel.Add(k.Substring(0, 4)); });
            cancel = cancel.Select(c => c.ToLower()).OrderBy(k => k).ToList();

            var files = new DirectoryInfo(@"daily\").GetFiles("*.csv").ToList();
            files.RemoveAll(f => cancel.Contains(f.Name.Substring(0, 4)));
            if (files.Count == 0) return;

            var tradingdays = TradingDays();
            var firstday = new Dictionary<string, DateTime>();
            var lasttday = new Dictionary<string, DateTime>();

            foreach (var file in files)
            {
                var data = File.ReadAllLines(file.FullName).OrderBy(d => d).ToList();
                var lday = data.Last().ToDateTime();
                var fday = data.First().ToDateTime();

                File.WriteAllLines(file.FullName, data);

                lasttday.Add(file.Name, lday);
                firstday.Add(file.Name, fday);
            }

            foreach (var key in firstday.Keys)
            {
                var results = lasttday.Where(d =>
                    {
                        var fday = firstday[key];
                        var pday = firstday[key].AddDays(-1);
                        while (!tradingdays.Result.Contains(pday.ToString("yyyyMMdd"))) pday = pday.AddDays(-1);
                        var ltfday = d.Value < fday;
                        var gtfday5 = d.Value >= pday;

                        return ltfday && gtfday5;
                    })
                    .ToDictionary(x => x.Key, y => y.Value);

                foreach (var result in results)
                {
                    var data = File.ReadAllLines(@"daily\" + result.Key).OrderBy(d => d).ToList();

                    // We count how many trading day there were between the first and the last days
                    // and calculate the frequency the symbol was traded
                    var count1 =
                        (double)(tradingdays.Result.IndexOf(data.Last().Substring(0, 8))) -
                        (double)(tradingdays.Result.IndexOf(data.First().Substring(0, 8)));

                    var freq1 = count1 == 0 ? 0 : (data.Count - 1) / count1;

                    data.RemoveAll(d => d.ToDateTime() < new DateTime(result.Value.Year - 1, 1, 1));

                    var count2 =
                        (double)(tradingdays.Result.IndexOf(data.Last().Substring(0, 8))) -
                        (double)(tradingdays.Result.IndexOf(data.First().Substring(0, 8)));

                    var freq2 = count2 == 0 ? 0 : (data.Count - 1) / count2;

                    if (Math.Max(freq1, freq2) < .5) continue;

                    var output = "{\"" + key.Replace(".csv", "\", \"") + result.Key.Replace(".csv", "\"},");

                    var outvalue = string.Empty;
                    //if (TickerChange.TryGetValue(key.Replace(".csv", "").ToUpper(), out outvalue))
                    //    if (outvalue == result.Key.Replace(".csv", "").ToUpper())
                    //    {
                    //        output += "//";
                    //        File.AppendAllText("mergedic.txt", output.ToUpper() + "\r\n");
                    //    }

                    File.AppendAllText("merge.txt", output.ToUpper() + "\r\n");
                }
            }
            Console.WriteLine(" Done!");
        }
        
        #region Utils
        private static bool Filter(string line)
        {
            int type;

            return !int.TryParse(line.Substring(16, 3), out type) || type < 3 || (type > 8 && type != 11)
                || line.Substring(12, 12).Trim().Contains(" ");
        }

        private static async Task<List<string>> TradingDays()
        {
            var ifile = new FileInfo("equity".Replace("equity\\bra", "market-hours") + "holidays-bra.csv");
            if (!ifile.Exists) await CreateHolidayFile();

            var data = File.ReadAllLines(ifile.FullName).ToList();
            data.RemoveAt(0);   // Remove header

            var tradingdays = new List<DateTime>();
            var holidays = data.Select(d => DateTime.ParseExact(d, "yyyy, MM, dd", _ptBR));

            var date = holidays.First();
            while (date < holidays.Last()) tradingdays.Add(date = date.AddDays(1));

            tradingdays = tradingdays.Except(holidays).ToList();
            tradingdays.RemoveAll(d => d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday);

            return tradingdays.Select(d => d.ToString("yyyyMMdd")).ToList();
        }

        private static async Task<List<string>> ReadAsyncZipFile(FileInfo zipfile, List<string> selected)
        {
            var data = new List<string>();

            if (!zipfile.Exists) return data;
            
            try
            {
                ZipFile zip;
                using (var reader = Compression.Unzip(zipfile.FullName, out zip))
                {
                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (selected.Count == 0 || selected.Any(s => line.Contains(s)))
                            data.Add(line);
                    }
                }

                //using (var zip2open = new FileStream(zipfile.FullName, FileMode.Open, FileAccess.Read))
                //using (var archive = new ZipArchive(zip2open, ZipArchiveMode.Read))
                //    foreach (var entry in archive.Entries)
                //        using (var file = new StreamReader(entry.Open()))
                //            while (!file.EndOfStream)
                //            {
                //                var line = await file.ReadLineAsync();
                //                if (selected.Count == 0 || selected.Any(s => line.Contains(s)))
                //                    data.Add(line);
                //            }
            }
            catch (Exception e) { Console.WriteLine(e.Message); }

            return data;
        }

        private static async Task<List<string>> ReadAsyncZipFile(FileInfo zipfile)
        {
            return await ReadAsyncZipFile(zipfile, selected: new List<string>());
        }

        private static async Task<string> DownloadAsync(string str)
        {
            try
            {
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(str))
                using (var content = response.Content)
                    return await content.ReadAsStringAsync();
            }
            catch (Exception e) { return e.Message; }
        }
        #endregion
    }
    public static class Extensions
    {
        /// <summary>
        /// Extension method for faster string to decimal conversion. 
        /// </summary>
        /// <param name="str">String to be converted to positive decimal value</param>
        /// <remarks>Method makes some assuptions - always numbers, no "signs" +,- etc.</remarks>
        /// <returns>Decimal value of the string</returns>
        public static decimal ToDecimal(this string str)
        {
            long value = 0;
            var decimalPlaces = 0;
            bool hasDecimals = false;

            for (var i = 0; i < str.Length; i++)
            {
                var ch = str[i];
                if (ch == '.')
                {
                    hasDecimals = true;
                    decimalPlaces = 0;
                }
                else
                {
                    value = value * 10 + (ch - '0');
                    decimalPlaces++;
                }
            }

            var lo = (int)value;
            var mid = (int)(value >> 32);
            return new decimal(lo, mid, 0, false, (byte)(hasDecimals ? decimalPlaces : 0));
        }

        /// <summary>
        /// Extension method for faster string to Int32 conversion. 
        /// </summary>
        /// <param name="str">String to be converted to positive Int32 value</param>
        /// <remarks>Method makes some assuptions - always numbers, no "signs" +,- etc.</remarks>
        /// <returns>Int32 value of the string</returns>
        public static int ToInt32(this string str)
        {
            int value = 0;
            for (var i = 0; i < str.Length; i++)
            {
                value = value * 10 + (str[i] - '0');
            }
            return value;
        }

        /// <summary>
        /// Extension method for faster string to Int64 conversion. 
        /// </summary>
        /// <param name="str">String to be converted to positive Int64 value</param>
        /// <remarks>Method makes some assuptions - always numbers, no "signs" +,- etc.</remarks>
        /// <returns>Int32 value of the string</returns>
        public static long ToInt64(this string str)
        {
            long value = 0;
            for (var i = 0; i < str.Length; i++)
            {
                value = value * 10 + (str[i] - '0');
            }
            return value;
        }

        public static DateTime ToDateTime(this string str, int startIndex)
        {
            try
            {
                return DateTime.ParseExact(str.Substring(startIndex, 8), "yyyyMMdd", CultureInfo.CreateSpecificCulture("en-US"));
            }
            catch (Exception)
            {
                return new DateTime(1978, 6, 8);
            }
        }

        public static DateTime ToDateTime(this string str)
        {
            return str.ToDateTime(0);
        }
    }
}
