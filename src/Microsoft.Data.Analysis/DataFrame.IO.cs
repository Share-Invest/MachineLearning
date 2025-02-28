﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Data.Analysis
{
    public partial class DataFrame
    {
        private const int DefaultStreamReaderBufferSize = 1024;

        private static Type GuessKind(int col, List<string[]> read)
        {
            Type res = typeof(string);
            int nbline = 0;
            foreach (var line in read)
            {
                if (col >= line.Length)
                    throw new FormatException(string.Format(Strings.LessColumnsThatExpected, nbline + 1));

                string val = line[col];

                if (string.Equals(val, "null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(val))
                {
                    bool boolParse = bool.TryParse(val, out bool boolResult);
                    if (boolParse)
                    {
                        res = DetermineType(nbline == 0, typeof(bool), res);
                        ++nbline;
                        continue;
                    }
                    bool floatParse = float.TryParse(val, out float floatResult);
                    if (floatParse)
                    {
                        res = DetermineType(nbline == 0, typeof(float), res);
                        ++nbline;
                        continue;
                    }
                    bool dateParse = DateTime.TryParse(val, out DateTime dateResult);
                    if (dateParse)
                    {
                        res = DetermineType(nbline == 0, typeof(DateTime), res);
                        ++nbline;
                        continue;
                    }

                    res = DetermineType(nbline == 0, typeof(string), res);
                    ++nbline;
                }
            }
            return res;
        }

        private static Type DetermineType(bool first, Type suggested, Type previous)
        {
            if (first)
                return suggested;
            else
                return MaxKind(suggested, previous);
        }

        private static Type MaxKind(Type a, Type b)
        {
            if (a == typeof(string) || b == typeof(string))
                return typeof(string);
            if (a == typeof(float) || b == typeof(float))
                return typeof(float);
            if (a == typeof(bool) || b == typeof(bool))
                return typeof(bool);
            if (a == typeof(DateTime) || b == typeof(DateTime))
                return typeof(DateTime);
            return typeof(string);
        }

        /// <summary>
        /// Reads a text file as a DataFrame.
        /// </summary>
        /// <param name="filename">filename</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="columnNames">column names (can be empty)</param>
        /// <param name="dataTypes">column types (can be empty)</param>
        /// <param name="numRows">number of rows to read</param>
        /// <param name="guessRows">number of rows used to guess types</param>
        /// <param name="addIndexColumn">add one column with the row index</param>
        /// <param name="encoding">The character encoding. Defaults to UTF8 if not specified</param>
        /// <returns>DataFrame</returns>
        public static DataFrame LoadCsv(string filename,
                                char separator = ',', bool header = true,
                                string[] columnNames = null, Type[] dataTypes = null,
                                int numRows = -1, int guessRows = 10,
                                bool addIndexColumn = false, Encoding encoding = null)
        {
            using (Stream fileStream = new FileStream(filename, FileMode.Open))
            {
                return LoadCsv(fileStream,
                                  separator: separator, header: header, columnNames: columnNames, dataTypes: dataTypes, numberOfRowsToRead: numRows,
                                  guessRows: guessRows, addIndexColumn: addIndexColumn, encoding: encoding);
            }
        }

        public static DataFrame LoadFrom(IEnumerable<IList<object>> vals, IList<(string, Type)> columnInfos)
        {
            var columnsCount = columnInfos.Count;
            var columns = new List<DataFrameColumn>(columnsCount);

            foreach (var (name, type) in columnInfos)
            {
                var column = CreateColumn(type, name);
                columns.Add(column);
            }

            var res = new DataFrame(columns);

            foreach (var items in vals)
            {
                for (var c = 0; c < items.Count; c++)
                {
                    items[c] = items[c];
                }
                res.Append(items, inPlace: true);
            }

            return res;
        }

        public void SaveTo(DataTable table)
        {
            var columnsCount = Columns.Count;

            if (table.Columns.Count == 0)
            {
                foreach (var column in Columns)
                {
                    table.Columns.Add(column.Name, column.DataType);
                }
            }
            else
            {
                if (table.Columns.Count != columnsCount)
                    throw new ArgumentException();
                for (var c = 0; c < columnsCount; c++)
                {
                    if (table.Columns[c].DataType != Columns[c].DataType)
                        throw new ArgumentException();
                }
            }

            var items = new object[columnsCount];
            foreach (var row in Rows)
            {
                for (var c = 0; c < columnsCount; c++)
                {
                    items[c] = row[c] ?? DBNull.Value;
                }
                table.Rows.Add(items);
            }
        }

        public DataTable ToTable()
        {
            var res = new DataTable();
            SaveTo(res);
            return res;
        }

        public static DataFrame FromSchema(DbDataReader reader)
        {
            var columnsCount = reader.FieldCount;
            var columns = new DataFrameColumn[columnsCount];

            for (var c = 0; c < columnsCount; c++)
            {
                var type = reader.GetFieldType(c);
                var name = reader.GetName(c);
                var column = CreateColumn(type, name);
                columns[c] = column;
            }

            var res = new DataFrame(columns);
            return res;
        }

        public static async Task<DataFrame> LoadFrom(DbDataReader reader)
        {
            var res = FromSchema(reader);
            var columnsCount = reader.FieldCount;

            var items = new object[columnsCount];
            while (await reader.ReadAsync())
            {
                for (var c = 0; c < columnsCount; c++)
                {
                    items[c] = reader.IsDBNull(c)
                        ? null
                        : reader[c];
                }
                res.Append(items, inPlace: true);
            }

            reader.Close();

            return res;
        }

        public static async Task<DataFrame> LoadFrom(DbDataAdapter adapter)
        {
            using var reader = await adapter.SelectCommand.ExecuteReaderAsync();
            return await LoadFrom(reader);
        }

        public void SaveTo(DbDataAdapter dataAdapter, DbProviderFactory factory)
        {
            using var commandBuilder = factory.CreateCommandBuilder();
            commandBuilder.DataAdapter = dataAdapter;
            dataAdapter.InsertCommand = commandBuilder.GetInsertCommand();
            dataAdapter.UpdateCommand = commandBuilder.GetUpdateCommand();
            dataAdapter.DeleteCommand = commandBuilder.GetDeleteCommand();

            using var table = ToTable();

            var connection = dataAdapter.SelectCommand.Connection;
            var needClose = connection.TryOpen();

            try
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    dataAdapter.Update(table);
                }
                catch
                {
                    transaction.Rollback();
                    transaction.Dispose();
                    throw;
                }
                transaction.Commit();
            }
            finally
            {
                if (needClose)
                    connection.Close();
            }
        }

        /// <summary>
        /// return <paramref name="columnIndex"/> of <paramref name="columnNames"/> if not null or empty, otherwise return "Column{i}" where i is <paramref name="columnIndex"/>.
        /// </summary>
        /// <param name="columnNames">column names.</param>
        /// <param name="columnIndex">column index.</param>
        /// <returns></returns>

        private static string GetColumnName(string[] columnNames, int columnIndex)
        {
            var defaultColumnName = "Column" + columnIndex.ToString();
            if (columnNames is string[])
            {
                return string.IsNullOrEmpty(columnNames[columnIndex]) ? defaultColumnName : columnNames[columnIndex];
            }

            return defaultColumnName;
        }

        private static DataFrameColumn CreateColumn(Type kind, string columnName)
        {
            DataFrameColumn ret;
            if (kind == typeof(bool))
            {
                ret = new BooleanDataFrameColumn(columnName);
            }
            else if (kind == typeof(int))
            {
                ret = new Int32DataFrameColumn(columnName);
            }
            else if (kind == typeof(float))
            {
                ret = new SingleDataFrameColumn(columnName);
            }
            else if (kind == typeof(string))
            {
                ret = new StringDataFrameColumn(columnName, 0);
            }
            else if (kind == typeof(long))
            {
                ret = new Int64DataFrameColumn(columnName);
            }
            else if (kind == typeof(decimal))
            {
                ret = new DecimalDataFrameColumn(columnName);
            }
            else if (kind == typeof(byte))
            {
                ret = new ByteDataFrameColumn(columnName);
            }
            else if (kind == typeof(char))
            {
                ret = new CharDataFrameColumn(columnName);
            }
            else if (kind == typeof(double))
            {
                ret = new DoubleDataFrameColumn(columnName);
            }
            else if (kind == typeof(sbyte))
            {
                ret = new SByteDataFrameColumn(columnName);
            }
            else if (kind == typeof(short))
            {
                ret = new Int16DataFrameColumn(columnName);
            }
            else if (kind == typeof(uint))
            {
                ret = new UInt32DataFrameColumn(columnName);
            }
            else if (kind == typeof(ulong))
            {
                ret = new UInt64DataFrameColumn(columnName);
            }
            else if (kind == typeof(ushort))
            {
                ret = new UInt16DataFrameColumn(columnName);
            }
            else if (kind == typeof(DateTime))
            {
                ret = new PrimitiveDataFrameColumn<DateTime>(columnName);
            }
            else
            {
                throw new NotSupportedException(nameof(kind));
            }
            return ret;
        }

        private static DataFrameColumn CreateColumn(Type kind, string[] columnNames, int columnIndex)
        {
            return CreateColumn(kind, GetColumnName(columnNames, columnIndex));
        }

        private static DataFrame ReadCsvLinesIntoDataFrame(WrappedStreamReaderOrStringReader wrappedReader,
                                char separator = ',', bool header = true,
                                string[] columnNames = null, Type[] dataTypes = null,
                                long numberOfRowsToRead = -1, int guessRows = 10, bool addIndexColumn = false
                                )
        {
            if (dataTypes == null && guessRows <= 0)
            {
                throw new ArgumentException(string.Format(Strings.ExpectedEitherGuessRowsOrDataTypes, nameof(guessRows), nameof(dataTypes)));
            }

            List<DataFrameColumn> columns;
            string[] fields;
            using (var textReader = wrappedReader.GetTextReader())
            {
                TextFieldParser parser = new TextFieldParser(textReader);
                parser.SetDelimiters(separator.ToString());

                var linesForGuessType = new List<string[]>();
                long rowline = 0;
                int numberOfColumns = dataTypes?.Length ?? 0;

                if (header == true && numberOfRowsToRead != -1)
                {
                    numberOfRowsToRead++;
                }

                // First pass: schema and number of rows.
                while ((fields = parser.ReadFields()) != null)
                {
                    if ((numberOfRowsToRead == -1) || rowline < numberOfRowsToRead)
                    {
                        if (linesForGuessType.Count < guessRows || (header && rowline == 0))
                        {
                            if (header && rowline == 0)
                            {
                                if (columnNames == null)
                                {
                                    columnNames = fields;
                                }
                            }
                            else
                            {
                                linesForGuessType.Add(fields);
                                numberOfColumns = Math.Max(numberOfColumns, fields.Length);
                            }
                        }
                    }
                    ++rowline;
                    if (rowline == guessRows || guessRows == 0)
                    {
                        break;
                    }
                }

                if (rowline == 0)
                {
                    throw new FormatException(Strings.EmptyFile);
                }

                columns = new List<DataFrameColumn>(numberOfColumns);
                // Guesses types or looks up dataTypes and adds columns.
                for (int i = 0; i < numberOfColumns; ++i)
                {
                    Type kind = dataTypes == null ? GuessKind(i, linesForGuessType) : dataTypes[i];
                    columns.Add(CreateColumn(kind, columnNames, i));
                }
            }

            DataFrame ret = new DataFrame(columns);

            // Fill values.
            using (var textReader = wrappedReader.GetTextReader())
            {
                TextFieldParser parser = new TextFieldParser(textReader);
                parser.SetDelimiters(separator.ToString());

                long rowline = 0;
                while ((fields = parser.ReadFields()) != null && (numberOfRowsToRead == -1 || rowline < numberOfRowsToRead))
                {
                    if (header && rowline == 0)
                    {
                        // Skips.
                    }
                    else
                    {
                        ret.Append(fields, inPlace: true);
                    }
                    ++rowline;
                }

                if (addIndexColumn)
                {
                    PrimitiveDataFrameColumn<int> indexColumn = new PrimitiveDataFrameColumn<int>("IndexColumn", columns[0].Length);
                    for (int i = 0; i < columns[0].Length; i++)
                    {
                        indexColumn[i] = i;
                    }
                    columns.Insert(0, indexColumn);
                }

            }

            return ret;
        }

        private class WrappedStreamReaderOrStringReader
        {
            private readonly Stream _stream;
            private readonly long _initialPosition;
            private readonly Encoding _encoding;
            private readonly string _csvString;

            public WrappedStreamReaderOrStringReader(Stream stream, Encoding encoding)
            {
                _stream = stream;
                _initialPosition = stream.Position;
                _encoding = encoding;
                _csvString = null;
            }

            public WrappedStreamReaderOrStringReader(string csvString)
            {
                _csvString = csvString;
                _initialPosition = 0;
                _encoding = null;
                _stream = null;
            }

            // Returns a new TextReader. If the wrapped object is a stream, the stream is reset to its initial position. 
            public TextReader GetTextReader()
            {
                if (_stream != null)
                {
                    _stream.Seek(_initialPosition, SeekOrigin.Begin);
                    return new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: true, DefaultStreamReaderBufferSize, leaveOpen: true);
                }
                else
                {
                    return new StringReader(_csvString);
                }

            }

        }

        /// <summary>
        /// Reads CSV data passed in as a string into a DataFrame.
        /// </summary>
        /// <param name="csvString">csv data passed in as a string</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="columnNames">column names (can be empty)</param>
        /// <param name="dataTypes">column types (can be empty)</param>
        /// <param name="numberOfRowsToRead">number of rows to read not including the header(if present)</param>
        /// <param name="guessRows">number of rows used to guess types</param>
        /// <param name="addIndexColumn">add one column with the row index</param>
        /// <returns><see cref="DataFrame"/></returns>
        public static DataFrame LoadCsvFromString(string csvString,
                                char separator = ',', bool header = true,
                                string[] columnNames = null, Type[] dataTypes = null,
                                long numberOfRowsToRead = -1, int guessRows = 10, bool addIndexColumn = false)
        {
            WrappedStreamReaderOrStringReader wrappedStreamReaderOrStringReader = new WrappedStreamReaderOrStringReader(csvString);
            return ReadCsvLinesIntoDataFrame(wrappedStreamReaderOrStringReader, separator, header, columnNames, dataTypes, numberOfRowsToRead, guessRows, addIndexColumn);
        }

        /// <summary>
        /// Reads a seekable stream of CSV data into a DataFrame.
        /// </summary>
        /// <param name="csvStream">stream of CSV data to be read in</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="columnNames">column names (can be empty)</param>
        /// <param name="dataTypes">column types (can be empty)</param>
        /// <param name="numberOfRowsToRead">number of rows to read not including the header(if present)</param>
        /// <param name="guessRows">number of rows used to guess types</param>
        /// <param name="addIndexColumn">add one column with the row index</param>
        /// <param name="encoding">The character encoding. Defaults to UTF8 if not specified</param>
        /// <returns><see cref="DataFrame"/></returns>
        public static DataFrame LoadCsv(Stream csvStream,
                                char separator = ',', bool header = true,
                                string[] columnNames = null, Type[] dataTypes = null,
                                long numberOfRowsToRead = -1, int guessRows = 10, bool addIndexColumn = false,
                                Encoding encoding = null)
        {
            if (!csvStream.CanSeek)
            {
                throw new ArgumentException(Strings.NonSeekableStream, nameof(csvStream));
            }

            if (dataTypes == null && guessRows <= 0)
            {
                throw new ArgumentException(string.Format(Strings.ExpectedEitherGuessRowsOrDataTypes, nameof(guessRows), nameof(dataTypes)));
            }

            WrappedStreamReaderOrStringReader wrappedStreamReaderOrStringReader = new WrappedStreamReaderOrStringReader(csvStream, encoding ?? Encoding.UTF8);
            return ReadCsvLinesIntoDataFrame(wrappedStreamReaderOrStringReader, separator, header, columnNames, dataTypes, numberOfRowsToRead, guessRows, addIndexColumn);
        }

        /// <summary>
        /// Writes a DataFrame into a CSV.
        /// </summary>
        /// <param name="dataFrame"><see cref="DataFrame"/></param>
        /// <param name="path">CSV file path</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="encoding">The character encoding. Defaults to UTF8 if not specified</param>
        /// <param name="cultureInfo">culture info for formatting values</param>
        [Obsolete("WriteCsv is obsolete and will be removed in a future version. Use SaveCsv instead.")]
        public static void WriteCsv(DataFrame dataFrame, string path,
                                   char separator = ',', bool header = true,
                                   Encoding encoding = null, CultureInfo cultureInfo = null)
        {
            SaveCsv(dataFrame, path, separator, header, encoding, cultureInfo);
        }

        /// <summary>
        /// Saves a DataFrame into a CSV.
        /// </summary>
        /// <param name="dataFrame"><see cref="DataFrame"/></param>
        /// <param name="path">CSV file path</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="encoding">The character encoding. Defaults to UTF8 if not specified</param>
        /// <param name="cultureInfo">culture info for formatting values</param>
        public static void SaveCsv(DataFrame dataFrame, string path,
                                   char separator = ',', bool header = true,
                                   Encoding encoding = null, CultureInfo cultureInfo = null)
        {
            using (FileStream csvStream = new FileStream(path, FileMode.Create))
            {
                SaveCsv(dataFrame: dataFrame, csvStream: csvStream,
                           separator: separator, header: header,
                           encoding: encoding, cultureInfo: cultureInfo);
            }
        }

        /// <summary>
        /// Writes a DataFrame into a CSV.
        /// </summary>
        /// <param name="dataFrame"><see cref="DataFrame"/></param>
        /// <param name="csvStream">stream of CSV data to be write out</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="encoding">the character encoding. Defaults to UTF8 if not specified</param>
        /// <param name="cultureInfo">culture info for formatting values</param>
        [Obsolete("WriteCsv is obsolete and will be removed in a future version. Use SaveCsv instead.")]
        public static void WriteCsv(DataFrame dataFrame, Stream csvStream,
                           char separator = ',', bool header = true,
                           Encoding encoding = null, CultureInfo cultureInfo = null)
        {
            SaveCsv(dataFrame, csvStream, separator, header, encoding, cultureInfo);
        }

        /// <summary>
        /// Saves a DataFrame into a CSV.
        /// </summary>
        /// <param name="dataFrame"><see cref="DataFrame"/></param>
        /// <param name="csvStream">stream of CSV data to be write out</param>
        /// <param name="separator">column separator</param>
        /// <param name="header">has a header or not</param>
        /// <param name="encoding">the character encoding. Defaults to UTF8 if not specified</param>
        /// <param name="cultureInfo">culture info for formatting values</param>
        public static void SaveCsv(DataFrame dataFrame, Stream csvStream,
                        char separator = ',', bool header = true,
                        Encoding encoding = null, CultureInfo cultureInfo = null)
        {
            if (cultureInfo is null)
            {
                cultureInfo = CultureInfo.CurrentCulture;
            }

            if (cultureInfo.NumberFormat.NumberDecimalSeparator.Equals(separator.ToString()))
            {
                throw new ArgumentException("Decimal separator cannot match the column separator");
            }

            if (encoding is null)
            {
                encoding = Encoding.ASCII;
            }

            using (StreamWriter csvFile = new StreamWriter(csvStream, encoding, bufferSize: DefaultStreamReaderBufferSize, leaveOpen: true))
            {
                if (dataFrame != null)
                {
                    if (header)
                    {
                        SaveHeader(csvFile, dataFrame.Columns.GetColumnNames(), separator);
                    }

                    var record = new StringBuilder();

                    foreach (var row in dataFrame.Rows)
                    {
                        bool firstCell = true;
                        foreach (var cell in row)
                        {
                            if (!firstCell)
                            {
                                record.Append(separator);
                            }
                            else
                            {
                                firstCell = false;
                            }

                            Type t = cell?.GetType();

                            if (t == typeof(bool))
                            {
                                record.AppendFormat(cultureInfo, "{0}", cell);
                                continue;
                            }

                            if (t == typeof(float))
                            {
                                record.AppendFormat(cultureInfo, "{0:G9}", cell);
                                continue;
                            }

                            if (t == typeof(double))
                            {
                                record.AppendFormat(cultureInfo, "{0:G17}", cell);
                                continue;
                            }

                            if (t == typeof(decimal))
                            {
                                record.AppendFormat(cultureInfo, "{0:G31}", cell);
                                continue;
                            }

                            if (t == typeof(string))
                            {
                                string stringCell = (string)cell;
                                if (NeedsQuotes(stringCell, separator))
                                {
                                    record.Append('\"');
                                    record.Append(stringCell.Replace("\"", "\"\"")); // Quotations in CSV data must be escaped with another quotation
                                    record.Append('\"');
                                    continue;
                                }
                            }

                            record.Append(cell);
                        }

                        csvFile.WriteLine(record);

                        record.Clear();
                    }
                }
            }
        }

        private static void SaveHeader(StreamWriter csvFile, IReadOnlyList<string> columnNames, char separator)
        {
            bool firstColumn = true;
            foreach (string name in columnNames)
            {
                if (!firstColumn)
                {
                    csvFile.Write(separator);
                }
                else
                {
                    firstColumn = false;
                }

                if (NeedsQuotes(name, separator))
                {
                    csvFile.Write('\"');
                    csvFile.Write(name.Replace("\"", "\"\"")); // Quotations in CSV data must be escaped with another quotation
                    csvFile.Write('\"');
                }
                else
                {
                    csvFile.Write(name);
                }
            }
            csvFile.WriteLine();
        }

        private static bool NeedsQuotes(string csvCell, char separator)
        {
            return csvCell.AsSpan().IndexOfAny(separator, '\n', '\"') != -1;
        }
    }
}
