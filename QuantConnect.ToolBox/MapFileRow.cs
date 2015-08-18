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
 *
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuantConnect.ToolBox
{
    /// <summary>
    /// Represents a single row in a map_file. This is a csv file ordered as {date, mapped symbol}
    /// </summary>
    public class MapFileRow
    {
        /// <summary>
        /// Gets the date associated with this data
        /// </summary>
        public DateTime Date { get; private set; }

        /// <summary>
        /// Gets the mapped symbol
        /// </summary>
        public string MappedSymbol { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MapFileRow"/> class.
        /// </summary>
        public MapFileRow(DateTime date, string mappedSymbol)
        {
            Date = date;
            MappedSymbol = mappedSymbol.ToUpper();
        }

        /// <summary>
        /// Reads in the map_file for the specified equity symbol
        /// </summary>
        public static IEnumerable<MapFileRow> Read(string dataFolder, string symbol, string market)
        {
            var path = MapFile.GetMapFilePath(dataFolder, symbol, market);
            if (!File.Exists(path))
            {
                yield break;
            }

            foreach (var mapFileRow in Read(path))
            {
                yield return mapFileRow;
            }
        }

        /// <summary>
        /// Reads in the map_file at the specified path
        /// </summary>
        public static IEnumerable<MapFileRow> Read(string path)
        {
            foreach (var line in File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var csv = line.Split(',');
                yield return new MapFileRow(DateTime.ParseExact(csv[0], DateFormat.EightCharacter, null), csv[1]);
            }
        }
    }
}