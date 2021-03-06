﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace UdsFileReader
{
    public class UdsReader
    {
        private static readonly Encoding Encoding = Encoding.GetEncoding(1252);
        public const string FileExtension = ".uds";

        public enum SegmentType
        {
            Adp,
            Dtc,
            Ffmux,
            Ges,
            Mwb,
            Sot,
            Xpl,
        }

        public enum DataType
        {
            FloatScaled = 0,
            Binary1 = 1,
            Integer1 = 2,
            ValueName = 3,
            FixedEncoding = 4,
            Binary2 = 5,
            MuxTable = 6,
            HexBytes = 7,
            String = 8,
            HexScaled = 9,
            Integer2 = 10,
            Invalid = 0x3F,
        }

        public const int DataTypeMaskSwapped = 0x40;
        public const int DataTypeMaskSigned = 0x80;
        public const int DataTypeMaskEnum = 0x3F;

        public class ValueName
        {
            public ValueName(UdsReader udsReader, string[] lineArray)
            {
                LineArray = lineArray;

                if (lineArray.Length >= 5)
                {
                    try
                    {
                        string textMin = lineArray[1];
                        if (textMin.Length >= 2 && textMin.Length % 2 == 0 && !textMin.StartsWith("0x") && textMin.StartsWith("0"))
                        {
                            if (Int64.TryParse(textMin, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Int64 minValue))
                            {
                                MinValue = minValue;
                            }
                        }
                        else
                        {
                            if (textMin.Length < 34)
                            {
                                object valueObjMin = new Int64Converter().ConvertFromInvariantString(textMin);
                                if (valueObjMin != null)
                                {
                                    MinValue = (Int64)valueObjMin;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    try
                    {
                        string textMax = lineArray[2];
                        if (textMax.Length >= 2 && textMax.Length % 2 == 0 && !textMax.StartsWith("0x") && textMax.StartsWith("0"))
                        {
                            if (Int64.TryParse(textMax, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Int64 maxValue))
                            {
                                MaxValue = maxValue;
                            }
                        }
                        else
                        {
                            if (textMax.Length < 34)
                            {
                                object valueObjMax = new Int64Converter().ConvertFromInvariantString(textMax);
                                if (valueObjMax != null)
                                {
                                    MaxValue = (Int64) valueObjMax;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    if (UInt32.TryParse(lineArray[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 valueNameKey))
                    {
                        if (udsReader._textMap.TryGetValue(valueNameKey, out string[] nameValueArray))
                        {
                            NameArray = nameValueArray;
                        }
                    }
                }
            }

            public string[] LineArray { get; }
            public string[] NameArray { get; }
            public Int64? MinValue { get; }
            public Int64? MaxValue { get; }
        }

        public class MuxEntry
        {
            public MuxEntry(UdsReader udsReader, string[] lineArray)
            {
                LineArray = lineArray;

                if (lineArray.Length >= 8)
                {
                    if (string.Compare(lineArray[5], "D", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        Default = true;
                    }
                    else
                    {
                        if (Int32.TryParse(lineArray[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 minValue))
                        {
                            MinValue = minValue;
                        }
                    }

                    if (string.Compare(lineArray[6], "D", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        Default = true;
                    }
                    else
                    {
                        if (Int32.TryParse(lineArray[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 maxValue))
                        {
                            MaxValue = maxValue;
                        }
                    }
                    DataTypeEntry = new DataTypeEntry(udsReader, lineArray, 7);
                }
            }

            public string[] LineArray { get; }
            public bool Default { get; }
            public Int32? MinValue { get; }
            public Int32? MaxValue { get; }
            public DataTypeEntry DataTypeEntry { get; }
        }

        public class FixedEncodingEntry
        {
            public delegate string ConvertDelegate(UdsReader udsReader, byte[] data);

            public FixedEncodingEntry(UInt32[] keyArray, UInt32 dataLength, UInt32? unitKey = null, UInt32? numberOfDigits = null, double? scaleOffset = null, double? scaleMult = null, string unitExtra = null)
            {
                KeyArray = keyArray;
                DataLength = dataLength;
                UnitKey = unitKey;
                NumberOfDigits = numberOfDigits;
                ScaleOffset = scaleOffset;
                ScaleMult = scaleMult;
                UnitExtra = unitExtra;
            }

            public FixedEncodingEntry(UInt32[] keyArray, ConvertDelegate convertFunc)
            {
                KeyArray = keyArray;
                ConvertFunc = convertFunc;
            }

            public string ToString(UdsReader udsReader, byte[] data)
            {
                if (ConvertFunc != null)
                {
                    return ConvertFunc(udsReader, data);
                }

                if (DataLength == 0)
                {
                    return string.Empty;
                }

                if (data.Length < DataLength)
                {
                    return string.Empty;
                }

                UInt32 value;
                switch (DataLength)
                {
                    case 1:
                        value = data[0];
                        break;

                    case 2:
                        value = (UInt32) (data[0] << 8) | data[1];
                        break;

                    default:
                        return string.Empty;
                }

                double displayValue = value;
                if (ScaleOffset.HasValue)
                {
                    displayValue += ScaleOffset.Value;
                }
                if (ScaleMult.HasValue)
                {
                    displayValue *= ScaleMult.Value;
                }

                StringBuilder sb = new StringBuilder();
                UInt32 numberOfDigits = NumberOfDigits ?? 0;
                sb.Append(displayValue.ToString($"F{numberOfDigits}"));

                if (UnitKey.HasValue)
                {
                    sb.Append(" ");
                    sb.Append(GetUnitMapText(udsReader, UnitKey.Value) ?? string.Empty);
                }
                if (UnitExtra != null)
                {
                    sb.Append(" ");
                    sb.Append(UnitExtra);
                }

                return sb.ToString();
            }

            public UInt32[] KeyArray { get; }
            public UInt32 DataLength { get; }
            public UInt32? UnitKey { get; }
            public UInt32? NumberOfDigits { get; }
            public double? ScaleOffset { get; }
            public double? ScaleMult { get; }
            public string UnitExtra { get; }
            public ConvertDelegate ConvertFunc { get; }
        }

        public class DataTypeEntry
        {
            public DataTypeEntry(UdsReader udsReader, string[] lineArray, int offset)
            {
                UdsReader = udsReader;
                LineArray = lineArray;

                if (lineArray.Length >= offset + 10)
                {
                    if (!UInt32.TryParse(lineArray[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 dataTypeId))
                    {
                        throw new Exception("No data type id");
                    }
                    DataTypeId = dataTypeId;
                    DataType dataType = (DataType)(dataTypeId & DataTypeMaskEnum);

                    Int64? dataTypeExtra = null;
                    try
                    {
                        if (lineArray[offset].Length > 0)
                        {
                            object valueObjExtra = new Int64Converter().ConvertFromInvariantString(lineArray[offset]);
                            if (valueObjExtra != null)
                            {
                                dataTypeExtra = (Int64)valueObjExtra;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    if (UInt32.TryParse(lineArray[offset + 6], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 byteOffset))
                    {
                        ByteOffset = byteOffset;
                    }

                    if (UInt32.TryParse(lineArray[offset + 7], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 bitOffset))
                    {
                        BitOffset = bitOffset;
                    }

                    if (UInt32.TryParse(lineArray[offset + 8], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 bitLength))
                    {
                        BitLength = bitLength;
                    }

                    if (UInt32.TryParse(lineArray[offset + 9], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 nameDetailKey))
                    {
                        if (!udsReader._textMap.TryGetValue(nameDetailKey, out string[] nameDetailArray))
                        {
                            throw new Exception("No name detail found");
                        }
                        NameDetailArray = nameDetailArray;
                    }

                    if (UInt32.TryParse(lineArray[offset + 5], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 unitKey) && unitKey > 0)
                    {
                        if (!udsReader._unitMap.TryGetValue(unitKey, out string[] unitArray))
                        {
                            throw new Exception("No unit text found");
                        }
                        if (unitArray.Length < 1)
                        {
                            throw new Exception("No unit array too short");
                        }
                        UnitText = unitArray[0];
                    }

                    switch (dataType)
                    {
                        case DataType.FloatScaled:
                        case DataType.Integer1:
                        {
                            if (double.TryParse(lineArray[offset + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleOffset))
                            {
                                ScaleOffset = scaleOffset;
                            }

                            if (double.TryParse(lineArray[offset + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleMult))
                            {
                                ScaleMult = scaleMult;
                            }

                            if (double.TryParse(lineArray[offset + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleDiv))
                            {
                                ScaleDiv = scaleDiv;
                            }

                            NumberOfDigits = dataTypeExtra;
                            break;
                        }

                        case DataType.ValueName:
                        {
                            if (dataTypeExtra == null)
                            {
                                break;
                            }

                            NameValueList = new List<ValueName>();
                            IEnumerable<string[]> bitList = udsReader._ttdopLookup[(uint)dataTypeExtra.Value];
                            foreach (string[] ttdopArray in bitList)
                            {
                                if (ttdopArray.Length >= 5)
                                {
                                    NameValueList.Add(new ValueName(udsReader, ttdopArray));
                                }
                            }
                            break;
                        }

                        case DataType.FixedEncoding:
                        {
                            if (!UInt32.TryParse(lineArray[offset + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 fixedEncodingId))
                            {
                                throw new Exception("No fixed data type id");
                            }
                            FixedEncodingId = fixedEncodingId;

                            if (!udsReader._fixedEncodingMap.TryGetValue(fixedEncodingId, out FixedEncodingEntry fixedEncodingEntry))
                            {
                                break;
                            }

                            FixedEncoding = fixedEncodingEntry;
                            NumberOfDigits = fixedEncodingEntry.NumberOfDigits;
                            ScaleOffset = fixedEncodingEntry.ScaleOffset;
                            ScaleMult = fixedEncodingEntry.ScaleMult;

                            if (fixedEncodingEntry.UnitKey != null)
                            {
                                if (!udsReader._unitMap.TryGetValue(fixedEncodingEntry.UnitKey.Value, out string[] unitArray))
                                {
                                    throw new Exception("No unit text found");
                                }
                                if (unitArray.Length < 1)
                                {
                                    throw new Exception("No unit array too short");
                                }
                                UnitText = unitArray[0];
                            }
                            break;
                        }

                        case DataType.MuxTable:
                        {
                            if (dataTypeExtra == null)
                            {   // possible for lines with data length 0
                                break;
                            }

                            MuxEntryList = new List<MuxEntry>();
                            IEnumerable<string[]> muxList = udsReader._muxLookup[(uint)dataTypeExtra.Value];
                            foreach (string[] muxArray in muxList)
                            {
                                if (muxArray.Length >= 17)
                                {
                                    MuxEntryList.Add(new MuxEntry(udsReader, muxArray));
                                }
                            }
                            break;
                        }
                    }
                }
            }

            public UdsReader UdsReader { get; }
            public string[] LineArray { get; }
            public UInt32 DataTypeId { get; }
            public string[] NameDetailArray { get; }
            public Int64? NumberOfDigits { get; }
            public UInt32? FixedEncodingId { get; }
            public double? ScaleOffset { get; }
            public double? ScaleMult { get; }
            public double? ScaleDiv { get; }
            public string UnitText { get; }
            public UInt32? ByteOffset { get; }
            public UInt32? BitOffset { get; }
            public UInt32? BitLength { get; }
            public List<ValueName> NameValueList { get; }
            public List<MuxEntry> MuxEntryList { get; }
            public FixedEncodingEntry FixedEncoding { get; }

            public static string DataTypeIdToString(UInt32 dataTypeId)
            {
                UInt32 dataTypeEnum = dataTypeId & DataTypeMaskEnum;
                string dataTypeName = Enum.GetName(typeof(DataType), dataTypeEnum);
                // ReSharper disable once ConvertIfStatementToNullCoalescingExpression
                if (dataTypeName == null)
                {
                    dataTypeName = string.Format(CultureInfo.InvariantCulture, "{0}", dataTypeEnum);
                }

                if ((dataTypeId & DataTypeMaskSwapped) != 0x00)
                {
                    dataTypeName += " (Swapped)";
                }
                if ((dataTypeId & DataTypeMaskSigned) != 0x00)
                {
                    dataTypeName += " (Signed)";
                }

                return dataTypeName;
            }

            public string ToString(byte[] data)
            {
                if (data.Length == 0)
                {
                    return string.Empty;
                }
                UInt32 bitOffset = BitOffset ?? 0;
                UInt32 byteOffset = ByteOffset ?? 0;
                int bitLength = data.Length * 8;
                int byteLength = data.Length;
                if (BitLength.HasValue)
                {
                    bitLength = (int)BitLength.Value;
                    byteLength = (int) ((bitLength + bitOffset + 7) / 8);
                }
                if ((bitLength < 1) || (data.Length < byteOffset + byteLength))
                {
                    return string.Empty;
                }
                byte[] subData = new byte[byteLength];
                Array.Copy(data, byteOffset, subData, 0, byteLength);
                if (bitOffset > 0 || (bitLength & 0x7) != 0)
                {
                    BitArray bitArray = new BitArray(subData);
                    if (bitOffset > bitArray.Length)
                    {
                        return string.Empty;
                    }
                    // shift bits to left
                    for (int i = 0; i < bitArray.Length - bitOffset; i++)
                    {
                        bitArray[i] = bitArray[(int)(i + bitOffset)];
                    }
                    // clear unused bits
                    for (int i = bitLength; i < bitArray.Length; i++)
                    {
                        bitArray[i] = false;
                    }
                    bitArray.CopyTo(subData, 0);
                }

                StringBuilder sb = new StringBuilder();
                DataType dataType = (DataType) (DataTypeId & DataTypeMaskEnum);
                switch (dataType)
                {
                    case DataType.FloatScaled:
                    case DataType.HexScaled:
                    case DataType.Integer1:
                    case DataType.Integer2:
                    case DataType.ValueName:
                    case DataType.MuxTable:
                    {
                        UInt64 value = 0;
                        if ((DataTypeId & DataTypeMaskSwapped) != 0)
                        {
                            for (int i = 0; i < byteLength; i++)
                            {
                                value <<= 8;
                                value |= subData[byteLength - i - 1];
                            }
                        }
                        else
                        {
                            for (int i = 0; i < byteLength; i++)
                            {
                                value <<= 8;
                                value |= subData[i];
                            }
                        }

                        if (dataType == DataType.ValueName)
                        {
                            if (NameValueList == null)
                            {
                                return string.Empty;
                            }

                            foreach (ValueName valueName in NameValueList)
                            {
                                // ReSharper disable once ReplaceWithSingleAssignment.True
                                bool match = true;
                                if (valueName.MinValue.HasValue && (Int64)value < valueName.MinValue.Value)
                                {
                                    match = false;
                                }
                                if (valueName.MaxValue.HasValue && (Int64)value > valueName.MaxValue.Value)
                                {
                                    match = false;
                                }
                                if (match)
                                {
                                    if (valueName.NameArray != null && valueName.NameArray.Length > 0)
                                    {
                                        return valueName.NameArray[0];
                                    }
                                    return string.Empty;
                                }
                            }
                            return $"{GetTextMapText(UdsReader, 3455) ?? string.Empty}: {value}"; // Unbekannt
                        }

                        if (dataType == DataType.MuxTable)
                        {
                            if (MuxEntryList == null)
                            {
                                return string.Empty;
                            }

                            MuxEntry muxEntryDefault = null;
                            foreach (MuxEntry muxEntry in MuxEntryList)
                            {
                                if (muxEntry.Default)
                                {
                                    muxEntryDefault = muxEntry;
                                    continue;
                                }
                                // ReSharper disable once ReplaceWithSingleAssignment.True
                                bool match = true;
                                if (muxEntry.MinValue.HasValue && (Int64)value < muxEntry.MinValue.Value)
                                {
                                    match = false;
                                }
                                if (muxEntry.MaxValue.HasValue && (Int64)value > muxEntry.MaxValue.Value)
                                {
                                    match = false;
                                }
                                if (match)
                                {
                                    return muxEntry.DataTypeEntry.ToString(subData);
                                }
                            }

                            if (muxEntryDefault != null)
                            {
                                return muxEntryDefault.DataTypeEntry.ToString(subData);
                            }
                            return $"{GetTextMapText(UdsReader, 3455) ?? string.Empty}: {value}"; // Unbekannt
                        }

                        double scaledValue;
                        if ((DataTypeId & DataTypeMaskSigned) != 0)
                        {
                            UInt64 valueConv = value;
                            UInt64 signMask = (UInt64)1 << (bitLength - 1);
                            if ((signMask & value) != 0)
                            {
                                valueConv = (value ^ signMask) - signMask;  // sign extend
                            }
                            Int64 valueSigned = (Int64)valueConv;

                            if (dataType == DataType.Integer1 || dataType == DataType.Integer2)
                            {
                                sb.Append($"{valueSigned}");
                                break;
                            }
                            scaledValue = valueSigned;
                        }
                        else
                        {
                            if (dataType == DataType.Integer1 || dataType == DataType.Integer2)
                            {
                                sb.Append($"{value}");
                                break;
                            }
                            scaledValue = value;
                        }

                        try
                        {
                            if (ScaleMult.HasValue)
                            {
                                scaledValue *= ScaleMult.Value;
                            }
                            if (ScaleOffset.HasValue)
                            {
                                scaledValue += ScaleOffset.Value;
                            }
                            if (ScaleDiv.HasValue)
                            {
                                scaledValue /= ScaleDiv.Value;
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }

                        if (dataType == DataType.HexScaled)
                        {
                            sb.Append($"{(UInt64)scaledValue:X}");
                            break;
                        }

                        sb.Append(scaledValue.ToString($"F{NumberOfDigits ?? 0}"));
                        break;
                    }

                    case DataType.Binary1:
                    case DataType.Binary2:
                    {
                        foreach (var value in subData)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append(" ");
                            }
                            sb.Append(Convert.ToString(value, 2).PadLeft(8, '0'));
                        }
                        break;
                    }

                    case DataType.HexBytes:
                        sb.Append(BitConverter.ToString(subData).Replace("-", " "));
                        break;

                    case DataType.FixedEncoding:
                        return FixedEncoding.ToString(UdsReader, subData);

                    case DataType.String:
                        sb.Append(Encoding.GetString(subData));
                        break;

                    default:
                        return string.Empty;
                }

                if (!string.IsNullOrEmpty(UnitText))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(" ");
                    }
                    sb.Append(UnitText);
                }

                return sb.ToString();
            }
        }

        public class ParseInfoBase
        {
            public ParseInfoBase(string[] lineArray)
            {
                LineArray = lineArray;
            }

            public string[] LineArray { get; }
        }

        public class ParseInfoMwb : ParseInfoBase
        {
            public ParseInfoMwb(UInt32 serviceId, string[] lineArray, string[] nameArray, DataTypeEntry dataTypeEntry) : base(lineArray)
            {
                ServiceId = serviceId;
                NameArray = nameArray;
                DataTypeEntry = dataTypeEntry;
            }

            public UInt32 ServiceId { get; }
            public string[] NameArray { get; }
            public DataTypeEntry DataTypeEntry { get; }
        }

        private class SegmentInfo
        {
            public SegmentInfo(SegmentType segmentType, string segmentName, string fileName)
            {
                SegmentType = segmentType;
                SegmentName = segmentName;
                FileName = fileName;
            }

            public SegmentType SegmentType { get; }
            public string SegmentName { get; }
            public string FileName { get; }
            public List<string[]> LineList { set; get; }
        }

        private static readonly SegmentInfo[] SegmentInfos =
        {
            new SegmentInfo(SegmentType.Adp, "ADP", "RA"),
            new SegmentInfo(SegmentType.Dtc, "DTC", "RD"),
            new SegmentInfo(SegmentType.Ffmux, "FFMUX", "RF"),
            new SegmentInfo(SegmentType.Ges, "GES", "RG"),
            new SegmentInfo(SegmentType.Mwb, "MWB", "RM"),
            new SegmentInfo(SegmentType.Sot, "SOT", "RS"),
            new SegmentInfo(SegmentType.Xpl, "XPL", "RX"),
        };

        // simplified form without date handling
        private static readonly Dictionary<string, string> ChassisMapFixed = new Dictionary<string, string>()
        {
            { "1K", "VW36" },
            { "6R", "VW25" },
            { "3C", "VW46" },
            { "1T", "VW36" },
            { "6J", "SE25" },
            { "5N", "VW36" },
            { "AX", "VW36" },
            { "KE", "SE25" },
        };

        private Dictionary<string, string> _redirMap;
        private Dictionary<UInt32, string[]> _textMap;
        private Dictionary<UInt32, string[]> _unitMap;
        private Dictionary<UInt32, FixedEncodingEntry> _fixedEncodingMap;
        private ILookup<UInt32, string[]> _ttdopLookup;
        private ILookup<UInt32, string[]> _muxLookup;
        private Dictionary<string, string> _chassisMap;

        private static readonly Dictionary<byte, string> Type28Dict = new Dictionary<byte, string>()
        {
            {1, "OBD II (CARB)"},
            {2, "OBD (EPA)"},
            {3, "OBD + OBD II"},
            {4, "OBD I"},
            {6, "Euro-OBD"},
            {7, "EOBD + OBD II"},
            {8, "OBD + EOBD"},
            {9, "OBD+OBD II+EOBD"},
            {10, "JOBD"},
            {11, "JOBD + OBD II"},
            {12, "JOBD + EOBD"},
            {13, "JOBD+EOBD+OBD II"},
            {14, "HD Euro IV/B1"},
            {15, "HD Euro V/B2"},
            {16, "HD EURO EEC/C"},
            {17, "Eng. Manuf. Diag"},
            {18, "Eng. Manuf. Diag +"},
            {19, "HD OBD-C"},
            {20, "HD OBD"},
            {21, "WWH OBD"},
            {23, "HD EOBD-I"},
            {24, "HD EOBD-I M"},
            {25, "HD EOBD-II"},
            {26, "HD EOBD-II N"},
            {28, "OBDBr-1"},
            {29, "OBDBr-2"},
            {30, "KOBD"},
            {31, "IOBD I"},
            {32, "IOBD II"},
            {33, "HD EOBD-VI"},
            {34, "OBD+OBDII+HDOBD"},
            {35, "OBDBr-3"},
        };

        private static string GetTextMapText(UdsReader udsReader, UInt32 key)
        {
            if (udsReader._textMap.TryGetValue(key, out string[] nameArray1)) // Keine
            {
                if (nameArray1.Length > 0)
                {
                    return nameArray1[0];
                }
            }

            return null;
        }

        private static string GetUnitMapText(UdsReader udsReader, UInt32 key)
        {
            if (udsReader._unitMap.TryGetValue(key, out string[] nameArray1)) // Keine
            {
                if (nameArray1.Length > 0)
                {
                    return nameArray1[0];
                }
            }

            return null;
        }

        private static string Type2Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            byte value0 = data[0];
            if ((value0 & 0xC0) != 0)
            {
                sb.Append(((value0 & 0xC0) == 0x40) ? "C" : "U");
            }
            else
            {
                sb.Append("P");
            }

            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:X02}{1:X02}", value0 & 0x3F, data[1]));

            return sb.ToString();
        }

        private static string Type3Convert(UdsReader udsReader, byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                if (data.Length < i + 1)
                {
                    break;
                }
                int value = data[i] & 0x1F;
                if (value == 0)
                {
                    break;
                }

                UInt32 textKey;
                switch (value)
                {
                    case 1:
                        textKey = 152138;    // Regelkreis offen, Voraussetzungen für geschlossenen Regelkreis nicht erfüllt
                        break;

                    case 2:
                        textKey = 152137;    // Regelkreis geschlossen, benutze Lambdasonden
                        break;

                    case 4:
                        textKey = 152136;    // Regelkreis offen, wegen Fahrbedingungen
                        break;

                    case 8:
                        textKey = 152135;    // Regelkreis offen, wegen Systemfehler erkannt
                        break;

                    case 16:
                        textKey = 152134;    // Regelkreis geschlossen, aber Fehler Lambdasonde
                        break;

                    default:
                        textKey = 99014;    // Unbekannt
                        break;
                }

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(GetTextMapText(udsReader, textKey) ?? string.Empty);
            }

            return sb.ToString();
        }

        private static string Type18Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            int value = data[0] & 0x07;
            UInt32 textKey;
            switch (value)
            {
                case 1:
                    textKey = 167178;    // Vor erstem Katalysator
                    break;

                case 2:
                    textKey = 152751;    // Nach erstem Katalysator
                    break;

                case 3:
                    textKey = 159156;    // Außenluft/AUS
                    break;

                default:
                    textKey = 99014;    // Unbekannt
                    break;
            }

            return GetTextMapText(udsReader, textKey) ?? string.Empty;
        }

        private static string Type19Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            byte value = data[0];

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                if ((value & (1 << i)) != 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append($"B{(i >> 2) + 1}S{(i & 0x3) + 1}");
                }
            }

            return sb.ToString();
        }

        private static string Type20Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            double value1 = data[0] * 0.005;
            double value2 = (data[1] - 128.0) * 100.0 / 128.0;

            sb.Append($"{value1:0.000} ");
            sb.Append(GetUnitMapText(udsReader, 9) ?? string.Empty);  // V

            sb.Append("; ");
            sb.Append($"{value2:0.00} ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %
            // > 0 fett, < 0 mager

            return sb.ToString();
        }

        private static string Type28Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            byte value = data[0];
            if (Type28Dict.TryGetValue(value, out string text))
            {
                return text;
            }

            if (value == 5)
            {
                return GetTextMapText(udsReader, 98661) ?? string.Empty; // Keine
            }

            return GetTextMapText(udsReader, 99014) ?? string.Empty; // Unbekannt
        }

        private static string Type29Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            byte value = data[0];

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                if ((value & (1 << i)) != 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append($"B{(i >> 1) + 1}S{(i & 0x1) + 1}");
                }
            }

            return sb.ToString();
        }

        private static string Type30Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            UInt32 textKey;
            // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
            if ((data[0] & 0x01) != 0)
            {
                textKey = 098360;    // aktiv
            }
            else
            {
                textKey = 098671;    // inaktiv
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(GetTextMapText(udsReader, 064207) ?? string.Empty);   // Nebenantrieb
            sb.Append(" ");
            sb.Append(GetTextMapText(udsReader, textKey) ?? string.Empty);

            return sb.ToString();
        }

        private static string Type37_43a52_59Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            double value1 = (data[1] | (data[0] << 8)) / 32783.0;
            sb.Append($"{value1:0.000} ");
            sb.Append(GetUnitMapText(udsReader, 113) ?? string.Empty);  // Lambda

            double value2 = ((data[3] | (data[2] << 8)) - 32768.0) / 256.0;
            sb.Append($"{value2:0.000} ");
            sb.Append(GetUnitMapText(udsReader, 123) ?? string.Empty);  // mA

            return sb.ToString();
        }

        private static string Type50Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            UInt32 value = (UInt32)(data[1] | ((data[0]) << 8));
            double displayValue = (value & 0x7FFF) / 4.0;
            if ((value & 0x8000) != 0)
            {
                displayValue = -displayValue;
            }
            sb.Append($"{displayValue:0.} ");
            sb.Append(GetUnitMapText(udsReader, 79) ?? string.Empty);  // Pa

            return sb.ToString();
        }

        private static string Type60_63Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            double value = (data[1] | (data[0] << 8)) * 0.1 - 40.0;
            sb.Append($"{value:0.000} ");
            sb.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

            return sb.ToString();
        }

        private static string Type77_78Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            int value = (data[1] | (data[0] << 8));
            return $"{value / 60}h {value % 60}min";
        }

        private static string Type81Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            UInt32 textKey;
            byte value = data[0];
            if (value > 8)
            {
                value -= 8;
                sb.Append("Bifuel: ");
            }

            switch (value)
            {
                case 1:
                    textKey = 018273;   // Benzin
                    break;

                case 2:
                    textKey = 152301;   // Methanol
                    break;

                case 3:
                    textKey = 016086;   // Ethanol
                    break;

                case 4:
                    textKey = 000586;   // Diesel
                    break;

                case 5:
                    textKey = 090173;   // LPG
                    break;

                case 6:
                    textKey = 090209;   // CNG
                    break;

                case 7:
                    textKey = 167184;   // Propan
                    break;

                case 8:
                    textKey = 022443;   // Batterie / elektrisch
                    break;

                default:
                    return string.Empty;
            }

            sb.Append(GetTextMapText(udsReader, textKey) ?? string.Empty);

            return sb.ToString();
        }

        private static string Type84Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            UInt32 value = (UInt32) (data[1] | ((data[0]) << 8));
            double displayValue = value & 0x7FFF;
            if ((value & 0x8000) != 0)
            {
                displayValue = -displayValue;
            }
            sb.Append($"{displayValue:0.} ");
            sb.Append(GetUnitMapText(udsReader, 79) ?? string.Empty);  // Pa

            return sb.ToString();
        }

        private static string Type85_88Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            double value1 = (data[0] - 128.0) * 100.0 / 128.0;
            sb.Append($"{value1:0.0}");
            if (data[1] != 0)
            {
                sb.Append("/");
                double value2 = (data[1] - 128.0) * 100.0 / 128.0;
                sb.Append($"{value2:0.0}");
            }
            sb.Append(" ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

            return sb.ToString();
        }

        private static string Type95Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            byte value = data[0];

            switch (value)
            {
                case 14:
                    return "HD Euro IV/B1";

                case 15:
                    return "HD Euro V/B2";

                case 16:
                    return "HD EURO EEC/C";
            }
            return GetTextMapText(udsReader, 99014) ?? string.Empty;  // Unbekannt
        }

        private static string Type100Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 5)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            double value1 = (data[0] - 125.0) * 0.01;
            double value2 = (data[1] - 125.0) * 0.01;
            sb.Append($"TQ_Max 1/2: {value1:0.}/{value2:0.} ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

            double value3 = (data[2] - 125.0) * 0.01;
            double value4 = (data[3] - 125.0) * 0.01;
            sb.Append("; ");
            sb.Append($"TQ_Max 3/4: {value3:0.}/{value4:0.} ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

            double value5 = (data[4] - 125.0) * 0.01;
            sb.Append("; ");
            sb.Append($"TQ_Max 5: {value5:0.} ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %
            return sb.ToString();
        }

        private static string Type101Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            byte valueData = data[1];
            StringBuilder sb = new StringBuilder();

            if ((maskData & 0x01) != 0)
            {
                sb.Append("PTO_STAT: ");
                sb.Append((valueData & 0x01) != 0 ? "ON" : "OFF");
            }

            if ((maskData & 0x02) != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("N/D_STAT: ");
                sb.Append((valueData & 0x02) != 0 ? "NEUTR" : "DRIVE");
            }

            if ((maskData & 0x04) != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("MT_GEAR: ");
                sb.Append((valueData & 0x04) != 0 ? "NEUTR" : "GEAR");
            }
            return sb.ToString();
        }

        private static string Type102Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbValue = new StringBuilder();
                if ((maskData & (1 << i)) != 0)
                {
                    char name = (char)('A' + i);
                    sbValue.Append($"MAF{name}: ");

                    int offset = i * 2 + 1;
                    int value = (data[offset] << 8) | data[offset + 1];
                    double displayValue = value / 32.0;

                    sbValue.Append($"{displayValue:0.00} ");
                    sbValue.Append(GetUnitMapText(udsReader, 26) ?? string.Empty); // g/s

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbValue);
                }
            }

            return sb.ToString();
        }

        private static string Type103Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                if ((maskData & (1 << i)) != 0)
                {
                    byte value = data[i + 1];
                    double displayValue = value - 40.0;

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append($"ECT {i + 1}: {displayValue:0.} ");
                    sb.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C
                }
            }

            return sb.ToString();
        }

        private static string Type104Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 6 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                StringBuilder sbValue = new StringBuilder();
                bool bData1 = (maskData & (1 << i)) != 0;
                bool bData2 = (maskData & (1 << (i + 3))) != 0;
                if (bData1 || bData2)
                {
                    sbValue.Append("IAT ");
                    if (bData1)
                    {
                        sbValue.Append($"1{i + 1}");
                    }
                    if (bData2)
                    {
                        if (bData1)
                        {
                            sbValue.Append("/");
                        }
                        sbValue.Append($"2{i + 1}");
                    }
                    sbValue.Append(": ");

                    if (bData1)
                    {
                        byte value = data[i + 1];
                        double displayValue = value - 40.0;
                        sbValue.Append($"{displayValue:0.}");
                    }
                    if (bData2)
                    {
                        byte value = data[i + 3 + 1];
                        double displayValue = value - 40.0;
                        if (bData1)
                        {
                            sbValue.Append("/");
                        }
                        sbValue.Append($"{displayValue:0.}");
                    }
                    sbValue.Append(" ");
                    sbValue.Append(GetUnitMapText(udsReader, 3) ?? string.Empty); // °C

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbValue);
                }
            }

            return sb.ToString();
        }

        private static string Type105Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 6 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbValue = new StringBuilder();
                char name = (char)('A' + i);
                sbValue.Append($"EGR {name}: ");
                for (int j = 0; j < 3; j++)
                {
                    byte value = data[index + 1];
                    double displayValue;
                    if (j < 2)
                    {
                        displayValue = (value - 128.0) * 100.0 / 128.0;
                    }
                    else
                    {
                        displayValue = value * 100.0 / 255.0;
                    }

                    if (j > 0)
                    {
                        sbValue.Append("/");
                    }
                    // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                    if ((maskData & (1 << index)) != 0)
                    {
                        sbValue.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbValue.Append("---");
                    }
                    index++;
                }
                sbValue.Append(" ");
                sbValue.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbValue);
            }

            return sb.ToString();
        }

        private static string Type106Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbValue = new StringBuilder();
                char name = (char)('A' + i);
                sbValue.Append($"IAF_{name} cmd/rel: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbValue.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        byte value = data[index + 1];
                        double displayValue = value * 100.0 / 255.0;
                        sbValue.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbValue.Append("---");
                    }

                    index++;
                }
                sbValue.Append(" ");
                sbValue.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbValue);
            }

            return sb.ToString();
        }

        private static string Type107Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                sbVal.Append($"EGR Temp {i + 1}1/{i + 1}2: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbVal.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        byte value = data[index + 1];
                        double displayValue = value - 40.0;
                        sbVal.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbVal.Append("---");
                    }
                    index++;
                }
                sbVal.Append(" ");
                sbVal.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type108Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                char name = (char)('A' + i);
                sbVal.Append($"THR {name} cmd/rel: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbVal.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        byte value = data[index + 1];
                        double displayValue = value * 100.0 / 255.0;
                        sbVal.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbVal.Append("---");
                    }
                    index++;
                }
                sbVal.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type110Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 * 2 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                char name = (char)('A' + i);
                sbVal.Append($"ICP_{name} cmd/rel: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbVal.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        int offset = index * 2 + 1;
                        int value = (data[offset] << 8) | data[offset + 1];
                        double displayValue = value * 10.0;
                        sbVal.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbVal.Append("---");
                    }
                    index++;
                }
                sbVal.Append(GetUnitMapText(udsReader, 79) ?? string.Empty);  // Pa

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type111Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                char name = (char)('A' + i);
                sbVal.Append($"TC{name}_PRESS: ");

                if ((maskData & (1 << i)) != 0)
                {
                    double displayValue = data[i + 1];
                    sbVal.Append($"{displayValue:0.} ");
                }
                else
                {
                    sbVal.Append("--- ");
                }
                sbVal.Append(GetUnitMapText(udsReader, 103) ?? string.Empty);  // kpa

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type112Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 8 + 2)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int offset = 1;
            for (int i = 0; i < 2; i++)
            {
                int bitStart = i * 3;
                int infoData = (maskData >> bitStart) & 0x7;
                bool cmd = (infoData & 0x01) != 0;
                bool act = (infoData & 0x02) != 0;
                bool status = (infoData & 0x04) != 0;
                StringBuilder sbType = new StringBuilder();
                if (cmd)
                {
                    sbType.Append("cmd");
                }
                if (act)
                {
                    if (sbType.Length > 0)
                    {
                        sbType.Append("/");
                    }
                    sbType.Append("act");
                }
                if (sbType.Length > 0)
                {
                    sbType.Append(": ");
                }

                StringBuilder sbValue = new StringBuilder();
                for (int j = 0; j < 2; j++)
                {
                    if ((infoData & (1 << j)) != 0)
                    {
                        double displayValue = (data[offset + 1] | (data[offset] << 8)) / 32.0;
                        if (sbValue.Length > 0)
                        {
                            sbValue.Append("/");
                        }
                        sbValue.Append($"{displayValue:0.}");
                    }

                    offset += 2;
                }
                if (sbValue.Length > 0)
                {
                    sbValue.Append(" ");
                    sbValue.Append(GetUnitMapText(udsReader, 103) ?? string.Empty); // kPa
                }

                StringBuilder sbStat = new StringBuilder();
                if (status)
                {
                    int value = (data[9] >> (i * 2)) & 0x03;
                    UInt32 textKey;
                    switch (value)
                    {
                        case 1:
                            textKey = 152138;    // Regelkreis offen
                            break;

                        case 2:
                            textKey = 152137;    // Regelkreis geschlossen
                            break;

                        case 3:
                            textKey = 101955;    // Fehler vorhanden
                            break;

                        default:
                            textKey = 99014;    // Unbekannt
                            break;
                    }
                    sbStat.Append(GetTextMapText(udsReader, textKey) ?? string.Empty);
                }

                if (sbType.Length > 0 || sbValue.Length > 0 || sbStat.Length > 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    char name = (char)('A' + i);
                    sb.Append($"BP_{name} ");
                    sb.Append(sbType);
                    sb.Append(sbValue);
                    if (sbStat.Length > 0)
                    {
                        sb.Append(" ");
                        sb.Append(sbStat);
                    }
                }
            }

            return sb.ToString();
        }

        private static string Type113Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbValue = new StringBuilder();

                char name = (char)('A' + i);
                sbValue.Append($"VGT_{name} cmd/act: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbValue.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        byte value = data[index + 1];
                        double displayValue = value * 100.0 / 255.0;
                        sbValue.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbValue.Append("---");
                    }

                    index++;
                }
                sbValue.Append(" ");
                sbValue.Append(GetUnitMapText(udsReader, 1) ?? string.Empty); // %

                StringBuilder sbStat = new StringBuilder();
                if((maskData & (1 << index)) != 0)
                {
                    int value = (data[5] >> (i * 2)) & 0x03;
                    UInt32 textKey;
                    switch (value)
                    {
                        case 1:
                            textKey = 152138;    // Regelkreis offen
                            break;

                        case 2:
                            textKey = 152137;    // Regelkreis geschlossen
                            break;

                        case 3:
                            textKey = 101955;    // Fehler vorhanden
                            break;

                        default:
                            textKey = 99014;    // Unbekannt
                            break;
                    }
                    sbStat.Append(GetTextMapText(udsReader, textKey) ?? string.Empty);
                }
                index++;

                if (sbValue.Length > 0 || sbStat.Length > 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbValue);
                    if (sbStat.Length > 0)
                    {
                        sb.Append(" ");
                        sb.Append(sbStat);
                    }
                }
            }

            return sb.ToString();
        }

        private static string Type114Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                char name = (char)('A' + i);
                sbVal.Append($"WG_{name} cmd/act: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbVal.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        byte value = data[index + 1];
                        double displayValue = value * 100.0 / 255.0;
                        sbVal.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbVal.Append("---");
                    }
                    index++;
                }
                sbVal.Append(" ");
                sbVal.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type115Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                if ((maskData & (1 << (i * 2))) != 0)
                {
                    StringBuilder sbVal = new StringBuilder();
                    sbVal.Append($"EP{i + 1}: ");

                    int offset = i * 2 + 1;
                    int value = (data[offset] << 8) | data[offset + 1];
                    double displayValue = value * 0.01;
                    sbVal.Append($"{displayValue:0.00} ");
                    sbVal.Append(GetUnitMapText(udsReader, 103) ?? string.Empty);  // kpa

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbVal);
                }
            }

            return sb.ToString();
        }

        private static string Type116Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                if ((maskData & (1 << (i * 2))) != 0)
                {
                    StringBuilder sbVal = new StringBuilder();
                    char name = (char)('A' + i);
                    sbVal.Append($"TC{name}_RPM: ");
                    int offset = i * 2 + 1;
                    double displayValue = (data[offset] << 8) | data[offset + 1];
                    sbVal.Append($"{displayValue:0.} ");
                    sbVal.Append(GetUnitMapText(udsReader, 21) ?? string.Empty);  // /min

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbVal);
                }
            }

            return sb.ToString();
        }

        private static string Type117_118Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 6 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();

            sb.Append(GetTextMapText(udsReader, 175748) ?? string.Empty);   // Kompressor
            sb.Append(" ");
            sb.Append(GetTextMapText(udsReader, 098311) ?? string.Empty);   // ein
            sb.Append("/");
            sb.Append(GetTextMapText(udsReader, 098310) ?? string.Empty);   // aus
            sb.Append(": ");

            for (int i = 0; i < 2; i++)
            {
                if (i > 0)
                {
                    sb.Append("/");
                }
                if ((maskData & (1 << i)) != 0)
                {
                    byte value = data[i + 1];
                    double displayValue = value - 40.0;
                    sb.Append($"{displayValue:0.}");
                }
                else
                {
                    sb.Append("---");
                }
            }
            sb.Append(" ");
            sb.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

            sb.Append("; ");
            sb.Append(GetTextMapText(udsReader, 175748) ?? string.Empty);   // Kompressor
            sb.Append(" ");
            sb.Append(GetTextMapText(udsReader, 098311) ?? string.Empty);   // ein
            sb.Append("/");
            sb.Append(GetTextMapText(udsReader, 098310) ?? string.Empty);   // aus
            sb.Append(": ");

            for (int i = 0; i < 2; i++)
            {
                if (i > 0)
                {
                    sb.Append("/");
                }
                if ((maskData & (1 << (i + 2))) != 0)
                {
                    int value = (data[(i *2) + 3] << 8) | data[(i * 2) + 4];
                    double displayValue = value * 0.1 - 40.0;
                    sb.Append($"{displayValue:0.}");
                }
                else
                {
                    sb.Append("---");
                }
            }
            sb.Append(" ");
            sb.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

            return sb.ToString();
        }

        private static string Type119Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbValue = new StringBuilder();
                int maskIndex = i * 2;
                bool b1 = (maskData & (1 << maskIndex)) != 0;
                bool b2 = (maskData & (1 << (maskIndex + 1))) != 0;
                int bNum = i + 1;
                if (b1 & b2)
                {
                    sbValue.Append($"B{bNum}S1/B{bNum}S2: ");
                }
                else if (b1)
                {
                    sbValue.Append($"B{bNum}S1: ");
                }
                else if (b2)
                {
                    sbValue.Append($"B{bNum}S2: ");
                }
                else
                {
                    continue;
                }

                if (b1)
                {
                    byte value = data[(i * 2) + 1];
                    double displayValue = value - 40.0;
                    sbValue.Append($"{displayValue:0.}");
                }
                if (b2)
                {
                    byte value = data[(i * 2) + 2];
                    double displayValue = value - 40.0;
                    if (b1)
                    {
                        sbValue.Append("/");
                    }
                    sbValue.Append($"{displayValue:0.}");
                }
                sbValue.Append(" ");
                sbValue.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbValue);
            }

            return sb.ToString();
        }

        private static string Type120_121Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 8 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append("/");
                }
                if ((maskData & (1 << i)) != 0)
                {
                    int value = (data[(i * 2) + 1] << 8) | data[(i * 2) + 2];
                    double displayValue = value * 0.1 - 40.0;
                    sb.Append($"{displayValue:0.} ");
                }
                else
                {
                    sb.Append("--- ");
                }
                sb.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C
            }

            sb.Insert(0, "S1/S2/S3/S4: ");

            return sb.ToString();
        }

        private static string Type122_123Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 6 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            sb.Append("Delta/In/Out: ");

            if ((maskData & 0x01) != 0)
            {
                int value = (data[1] << 8) | data[2];
                double displayValue = (value & 0x7FFF) * 0.01;
                if ((value & 0x8000) != 0)
                {
                    displayValue = -displayValue;
                }
                sb.Append($"{displayValue:0.00}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append("/");

            if ((maskData & 0x02) != 0)
            {
                int value = (data[3] << 8) | data[4];
                double displayValue = value * 0.01;
                sb.Append($"{displayValue:0.00}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append("/");

            if ((maskData & 0x02) != 0)
            {
                int value = (data[5] << 8) | data[6];
                double displayValue = value * 0.01;
                sb.Append($"{displayValue:0.00}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append(" ");

            sb.Append(GetUnitMapText(udsReader, 103) ?? string.Empty);  // kpa

            return sb.ToString();
        }

        private static string Type124Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 2 * 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                sbVal.Append($"B{i + 1}: ");

                for (int j = 0; j < 2; j++)
                {
                    if (j > 0)
                    {
                        sbVal.Append("/");
                    }
                    if ((maskData & (1 << index)) != 0)
                    {
                        int offset = index * 2 + 1;
                        int value = (data[offset] << 8) | data[offset + 1];
                        double displayValue = value * 0.1 - 40.0;
                        sbVal.Append($"{displayValue:0.}");
                    }
                    else
                    {
                        sbVal.Append("---");
                    }
                    index++;
                }
                sbVal.Append(" ");
                sbVal.Append(GetUnitMapText(udsReader, 3) ?? string.Empty);  // °C

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type125_126Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();

            if ((maskData & 0x01) != 0)
            {
                sb.Append("NTE:In");
            }

            if ((maskData & 0x02) != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("NTE:Out");
            }

            if ((maskData & 0x04) != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("NTE:Carve-out");
            }

            if ((maskData & 0x08) != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append("NTE:Def");
            }
            return sb.ToString();
        }

        private static string Type131Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append($"NOx{i + 1}1: ");
                if ((maskData & (1 << i)) != 0)
                {
                    int value = (data[(i * 2) + 1] << 8) | data[(i * 2) + 2];
                    double displayValue = value;
                    sb.Append($"{displayValue:0.} ");
                }
                else
                {
                    sb.Append("--- ");
                }
                sb.Append(GetUnitMapText(udsReader, 128) ?? string.Empty);  // ppm
            }

            return sb.ToString();
        }

        private static string Type127Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 3 * 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                if ((maskData & (1 << i)) != 0)
                {
                    StringBuilder sbValue = new StringBuilder();
                    switch (i)
                    {
                        case 1:
                            sbValue.Append(GetTextMapText(udsReader, 001565) ?? string.Empty);// Leerlauf
                            break;

                        case 2:
                            sbValue.Append("PTO");
                            break;

                        default:
                            sbValue.Append("Total");
                            break;
                    }
                    sbValue.Append(": ");

                    int offset = i * 4 + 1;
                    UInt32 value = (UInt32)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
                    sbValue.Append($"{value / 3600}H {value % 3600}s");

                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append(sbValue);
                }
            }

            return sb.ToString();
        }

        private static string Type129_130Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 5 * 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            int index = 0;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    StringBuilder sbValue = new StringBuilder();
                    if ((maskData & (1 << index)) != 0)
                    {
                        int offset = index * 4 + 1;
                        UInt32 value = (UInt32)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
                        if (sbValue.Length > 0)
                        {
                            sbValue.Append(" / ");
                        }
                        sbValue.Append($"{value / 3600}H {value % 3600}s");
                        if (i == 2)
                        {   // abort last round
                            break;
                        }
                    }

                    if (sbValue.Length > 0)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append("; ");
                        }
                        sb.Append(sbValue);
                    }

                    index++;
                }
            }

            return sb.ToString();
        }

        private static string Type133Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 10)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();

            sb.Append("ReAg Rate/Demand: ");
            if ((maskData & 0x01) != 0)
            {
                int value = (data[1] << 8) | data[2];
                double displayValue = value * 0.005;
                sb.Append($"{displayValue:0.}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append("/");

            if ((maskData & 0x02) != 0)
            {
                int value = (data[3] << 8) | data[4];
                double displayValue = value * 0.005;
                sb.Append($"{displayValue:0.}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append(" ");
            sb.Append(GetUnitMapText(udsReader, 110) ?? string.Empty);  // l/h

            sb.Append("; ");
            sb.Append("ReAg Level: ");
            if ((maskData & 0x04) != 0)
            {
                int value = data[5];
                double displayValue = value * 100 / 255.0;
                sb.Append($"{displayValue:0.}");
            }
            else
            {
                sb.Append("---");
            }
            sb.Append(" ");
            sb.Append(GetUnitMapText(udsReader, 1) ?? string.Empty);  // %

            sb.Append("; ");
            sb.Append("NWI ");
            sb.Append(GetTextMapText(udsReader, 099068) ?? string.Empty);   // Time
            sb.Append(": ");
            if ((maskData & 0x08) != 0)
            {
                UInt32 value = (UInt32) ((data[6] << 24) | (data[7] << 16) | (data[8] << 8) | data[9]);
                sb.Append($"{value / 3600}H {value % 3600}s");
            }
            else
            {
                sb.Append("---");
            }

            return sb.ToString();
        }

        private static string Type134Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                sbVal.Append($"PM{i + 1}1: ");
                if ((maskData & (1 << (i * 2))) != 0)
                {
                    int offset = i * 2 + 1;
                    int value = (data[offset] << 8) | data[offset + 1];
                    double displayValue = value / 80.0;
                    sbVal.Append($"{displayValue:0.00} ");
                    sbVal.Append(GetUnitMapText(udsReader, 127) ?? string.Empty);  // mg/m3
                }
                else
                {
                    sbVal.Append("---");
                }

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static string Type135Convert(UdsReader udsReader, byte[] data)
        {
            if (data.Length < 4 + 1)
            {
                return string.Empty;
            }

            byte maskData = data[0];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 2; i++)
            {
                StringBuilder sbVal = new StringBuilder();
                char name = (char)('A' + i);
                sbVal.Append($"MAP_{name}: ");
                if ((maskData & (1 << (i * 2))) != 0)
                {
                    int offset = i * 2 + 1;
                    int value = (data[offset] << 8) | data[offset + 1];
                    double displayValue = value / 32.0;
                    sbVal.Append($"{displayValue:0.00} ");
                    sbVal.Append(GetUnitMapText(udsReader, 103) ?? string.Empty);  // kPa abs
                    sbVal.Append(" abs");
                }
                else
                {
                    sbVal.Append("---");
                }

                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }
                sb.Append(sbVal);
            }

            return sb.ToString();
        }

        private static readonly FixedEncodingEntry[] FixedEncodingArray =
        {
            new FixedEncodingEntry(new UInt32[]{1, 65, 79, 80, 109, 136, 139, 140, 141, 142, 143, 152, 153, 159}, 0), // not existing
            new FixedEncodingEntry(new UInt32[]{2}, Type2Convert),
            new FixedEncodingEntry(new UInt32[]{3}, Type3Convert),
            new FixedEncodingEntry(new UInt32[]{4, 17, 44, 46, 47, 91}, 1, 1, 1, 0, 100.0 / 255), // Unit %
            new FixedEncodingEntry(new UInt32[]{5, 15, 70, 92, 132}, 1, 3, 0, -40, 1.0), // Unit °C
            new FixedEncodingEntry(new UInt32[]{6, 7, 8, 9, 45}, 1, 1, 1, -128, 100 / 128), // Unit %
            new FixedEncodingEntry(new UInt32[]{10}, 1, 103, 0, 0, 3.0, "rel"), // Unit kPa rel
            new FixedEncodingEntry(new UInt32[]{11, 51}, 1, 103, 0, null, null, "abs"), // Unit kPa abs
            new FixedEncodingEntry(new UInt32[]{12}, 2, 21, 0, 0, 0.25), // Unit /min
            new FixedEncodingEntry(new UInt32[]{13}, 1, 109, 0), // Unit km/h
            new FixedEncodingEntry(new UInt32[]{33, 49}, 2, 109, 0), // Unit km/h
            new FixedEncodingEntry(new UInt32[]{14}, 1, 1, 1, -128, 1 / 2.0), // Unit %
            new FixedEncodingEntry(new UInt32[]{16}, 2, 26, 2, 0, 0.01), // Unit g/s
            new FixedEncodingEntry(new UInt32[]{18}, Type18Convert),
            new FixedEncodingEntry(new UInt32[]{19}, Type19Convert),
            new FixedEncodingEntry(new UInt32[]{20}, Type20Convert),
            new FixedEncodingEntry(new UInt32[]{28}, Type28Convert),
            new FixedEncodingEntry(new UInt32[]{29}, Type29Convert),
            new FixedEncodingEntry(new UInt32[]{30}, Type30Convert),
            new FixedEncodingEntry(new UInt32[]{31}, 2, 8, 0), // Unit s
            new FixedEncodingEntry(new UInt32[]{34}, 2, 103, 2, 0, 0.8), // Unit kPa
            new FixedEncodingEntry(new UInt32[]{35}, 2, 103, 0, 0, 10.0, "rel"), // Unit kPa rel
            new FixedEncodingEntry(new UInt32[]{36, 37, 38, 39, 40, 41, 42, 43, 52, 53, 54, 55, 56, 57, 58, 59}, Type37_43a52_59Convert),
            new FixedEncodingEntry(new UInt32[]{48}, 1, null, 0),
            new FixedEncodingEntry(new UInt32[]{50}, Type50Convert),
            new FixedEncodingEntry(new UInt32[]{60, 61, 62, 63}, Type60_63Convert),
            new FixedEncodingEntry(new UInt32[]{77, 78}, Type77_78Convert),
            new FixedEncodingEntry(new UInt32[]{66}, 2, 9, 3, 0, 0.001), // Unit V
            new FixedEncodingEntry(new UInt32[]{67}, 2, 1, 0, 0, 100 / 255), // Unit %
            new FixedEncodingEntry(new UInt32[]{68}, 2, 113, 3, 0, 1.0 / 32783.0), // Unit Lambda
            new FixedEncodingEntry(new UInt32[]{69, 71, 72, 73, 74, 75, 76, 82, 90}, 1, 1, 0, 0, 100 / 255), // Unit %
            new FixedEncodingEntry(new UInt32[]{81}, Type81Convert),
            new FixedEncodingEntry(new UInt32[]{83}, 2, 103, 0, 0, 5.0, "abs"), // Unit kPa abs
            new FixedEncodingEntry(new UInt32[]{84}, Type84Convert),
            new FixedEncodingEntry(new UInt32[]{85, 86, 87, 88}, Type85_88Convert),
            new FixedEncodingEntry(new UInt32[]{89}, 2, 103, 0, 0, 10.0, "abs"), // Unit kPa abs
            new FixedEncodingEntry(new UInt32[]{93}, 2, 2, 2, -26880, 1 / 128.0), // Unit °
            new FixedEncodingEntry(new UInt32[]{94}, 2, 110, 2, 0, 1 / 20.0), // Unit l/h
            new FixedEncodingEntry(new UInt32[]{95}, Type95Convert),
            new FixedEncodingEntry(new UInt32[]{97, 98}, 1, 1, 0, -125, 1.0), // Unit %
            new FixedEncodingEntry(new UInt32[]{99}, 1, 7, 0), // Unit Nm
            new FixedEncodingEntry(new UInt32[]{100}, Type100Convert),
            new FixedEncodingEntry(new UInt32[]{101}, Type101Convert),
            new FixedEncodingEntry(new UInt32[]{102}, Type102Convert),
            new FixedEncodingEntry(new UInt32[]{103}, Type103Convert),
            new FixedEncodingEntry(new UInt32[]{104}, Type104Convert),
            new FixedEncodingEntry(new UInt32[]{105}, Type105Convert),
            new FixedEncodingEntry(new UInt32[]{106}, Type106Convert),
            new FixedEncodingEntry(new UInt32[]{107}, Type107Convert),
            new FixedEncodingEntry(new UInt32[]{108}, Type108Convert),
            new FixedEncodingEntry(new UInt32[]{110}, Type110Convert),
            new FixedEncodingEntry(new UInt32[]{111}, Type111Convert),
            new FixedEncodingEntry(new UInt32[]{112}, Type112Convert),
            new FixedEncodingEntry(new UInt32[]{113}, Type113Convert),
            new FixedEncodingEntry(new UInt32[]{114}, Type114Convert),
            new FixedEncodingEntry(new UInt32[]{115}, Type115Convert),
            new FixedEncodingEntry(new UInt32[]{116}, Type116Convert),
            new FixedEncodingEntry(new UInt32[]{117, 118}, Type117_118Convert),
            new FixedEncodingEntry(new UInt32[]{119}, Type119Convert),
            new FixedEncodingEntry(new UInt32[]{120, 121}, Type120_121Convert),
            new FixedEncodingEntry(new UInt32[]{122, 123}, Type122_123Convert),
            new FixedEncodingEntry(new UInt32[]{124}, Type124Convert),
            new FixedEncodingEntry(new UInt32[]{125, 126}, Type125_126Convert),
            new FixedEncodingEntry(new UInt32[]{127}, Type127Convert),
            new FixedEncodingEntry(new UInt32[]{129, 130}, Type129_130Convert),
            new FixedEncodingEntry(new UInt32[]{131}, Type131Convert),
            new FixedEncodingEntry(new UInt32[]{133}, Type133Convert),
            new FixedEncodingEntry(new UInt32[]{134}, Type134Convert),
            new FixedEncodingEntry(new UInt32[]{135}, Type135Convert),
        };

        public bool Init(string dirName)
        {
            try
            {
                List<string[]> redirList = ExtractFileSegment(new List<string> {Path.Combine(dirName, "ReDir" + FileExtension)}, "DIR");
                if (redirList == null)
                {
                    return false;
                }

                _redirMap = new Dictionary<string, string>();
                foreach (string[] redirArray in redirList)
                {
                    if (redirArray.Length != 3)
                    {
                        return false;
                    }
                    _redirMap.Add(redirArray[1].ToUpperInvariant(), redirArray[2]);
                }

                _textMap = CreateTextDict(dirName, "TTText*" + FileExtension, "TXT");
                if (_textMap == null)
                {
                    return false;
                }

                _unitMap = CreateTextDict(dirName, "Unit*" + FileExtension, "UNT");
                if (_unitMap == null)
                {
                    return false;
                }

                _fixedEncodingMap = new Dictionary<uint, FixedEncodingEntry>();
                foreach (FixedEncodingEntry fixedEncoding in FixedEncodingArray)
                {
                    foreach (UInt32 key in fixedEncoding.KeyArray)
                    {
                        _fixedEncodingMap[key] = fixedEncoding;
                    }
                }

                List<string[]> ttdopList = ExtractFileSegment(new List<string> { Path.Combine(dirName, "TTDOP" + FileExtension) }, "DOP");
                if (ttdopList == null)
                {
                    return false;
                }
                _ttdopLookup = ttdopList.ToLookup(item => UInt32.Parse(item[0]));

                List<string[]> muxList = ExtractFileSegment(new List<string> { Path.Combine(dirName, "MUX" + FileExtension) }, "MUX");
                if (muxList == null)
                {
                    return false;
                }
                _muxLookup = muxList.ToLookup(item => UInt32.Parse(item[0]));

                _chassisMap = CreateChassisDict(Path.Combine(dirName, "Chassis" + DataReader.FileExtension));
                if (_chassisMap == null)
                {
                    return false;
                }

                foreach (SegmentInfo segmentInfo in SegmentInfos)
                {
                    string fileName = Path.Combine(dirName, Path.ChangeExtension(segmentInfo.FileName, FileExtension));
                    List<string[]> lineList = ExtractFileSegment(new List<string> {fileName}, segmentInfo.SegmentName);
                    if (lineList == null)
                    {
                        return false;
                    }

                    segmentInfo.LineList = lineList;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string TestFixedTypes()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FixedEncodingEntry entry in FixedEncodingArray)
            {
                sb.Append($"{entry.KeyArray[0]}:");
                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0x10 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0x10, 0x20 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0xFF, 0x10 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0xFF, 0x10, 0x20 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0xFF, 0xAB, 0xCD }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(entry.ToString(this, new byte[] { 0xFF, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89 }));
                sb.Append("\"");

                sb.AppendLine();
            }
            return sb.ToString();
        }

        public List<ParseInfoBase> ExtractFileSegment(List<string> fileList, SegmentType segmentType)
        {
            SegmentInfo segmentInfoSel = null;
            foreach (SegmentInfo segmentInfo in SegmentInfos)
            {
                if (segmentInfo.SegmentType == segmentType)
                {
                    segmentInfoSel = segmentInfo;
                    break;
                }
            }

            if (segmentInfoSel?.LineList == null)
            {
                return null;
            }

            List<string[]> lineList = ExtractFileSegment(fileList, segmentInfoSel.SegmentName);
            if (lineList == null)
            {
                return null;
            }

            List<ParseInfoBase> resultList = new List<ParseInfoBase>();
            foreach (string[] line in lineList)
            {
                if (line.Length < 2)
                {
                    return null;
                }

                if (!UInt32.TryParse(line[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 value))
                {
                    return null;
                }

                if (value < 1 || value > segmentInfoSel.LineList.Count)
                {
                    return null;
                }

                string[] lineArray = segmentInfoSel.LineList[(int) value - 1];

                ParseInfoBase parseInfo;
                switch (segmentType)
                {
                    case SegmentType.Mwb:
                    {
                        if (lineArray.Length < 14)
                        {
                            return null;
                        }
                        if (!UInt32.TryParse(lineArray[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 nameKey))
                        {
                            return null;
                        }

                        if (!_textMap.TryGetValue(nameKey, out string[] nameArray))
                        {
                            return null;
                        }

                        if (!UInt32.TryParse(lineArray[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 serviceId))
                        {
                            return null;
                        }

                        DataTypeEntry dataTypeEntry;
                        try
                        {
                            dataTypeEntry = new DataTypeEntry(this, lineArray, 2);
                        }
                        catch (Exception)
                        {
                            return null;
                        }

                        parseInfo = new ParseInfoMwb(serviceId, lineArray, nameArray, dataTypeEntry);
                        break;
                    }

                    default:
                        parseInfo = new ParseInfoBase(lineArray);
                        break;
                }
                resultList.Add(parseInfo);
            }

            return resultList;
        }

        public static Dictionary<uint, string[]> CreateTextDict(string dirName, string fileSpec, string segmentName)
        {
            try
            {
                string[] files = Directory.GetFiles(dirName, fileSpec, SearchOption.TopDirectoryOnly);
                if (files.Length != 1)
                {
                    return null;
                }
                List<string[]> textList = ExtractFileSegment(files.ToList(), segmentName);
                if (textList == null)
                {
                    return null;
                }

                Dictionary<uint, string[]> dict = new Dictionary<uint, string[]>();
                foreach (string[] textArray in textList)
                {
                    if (textArray.Length < 2)
                    {
                        return null;
                    }
                    if (!UInt32.TryParse(textArray[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 key))
                    {
                        return null;
                    }

                    dict.Add(key, textArray.Skip(1).ToArray());
                }

                return dict;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, string> CreateChassisDict(string fileName)
        {
            try
            {
                List<string[]> textList = DataReader.ReadFileLines(fileName);
                if (textList == null)
                {
                    return null;
                }

                Dictionary<string, string> dict = new Dictionary<string, string>(ChassisMapFixed);
                foreach (string[] textArray in textList)
                {
                    if (textArray.Length != 2)
                    {
                        continue;
                    }

                    if (textArray[0].Length != 2)
                    {
                        continue;
                    }

                    string key = textArray[0];
                    string value = textArray[1];
                    if (dict.TryGetValue(key, out string dictValue))
                    {
                        if (string.Compare(dictValue, value, StringComparison.Ordinal) != 0)
                        {
                            // inconistent data base entry, ignore it!
                        }
                    }
                    else
                    {
                        dict.Add(key, value);
                    }
                }

                return dict;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string GetMd5Hash(string text)
        {
            //Prüfen ob Daten übergeben wurden.
            if ((text == null) || (text.Length == 0))
            {
                return string.Empty;
            }

            //MD5 Hash aus dem String berechnen. Dazu muss der string in ein Byte[]
            //zerlegt werden. Danach muss das Resultat wieder zurück in ein string.
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] textToHash = Encoding.Default.GetBytes(text);
            byte[] result = md5.ComputeHash(textToHash);

            return BitConverter.ToString(result).Replace("-", "");
        }

        public static List<string[]> ExtractFileSegment(List<string> fileList, string segmentName)
        {
            string segmentStart = "[" + segmentName + "]";
            string segmentEnd = "[/" + segmentName + "]";

            List<string[]> lineList = new List<string[]>();
            foreach (string fileName in fileList)
            {
                ZipFile zf = null;
                try
                {
                    Stream zipStream = null;
                    string fileNameBase = Path.GetFileName(fileName);
                    FileStream fs = File.OpenRead(fileName);
                    zf = new ZipFile(fs)
                    {
                        Password = GetMd5Hash(Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant())
                    };
                    foreach (ZipEntry zipEntry in zf)
                    {
                        if (!zipEntry.IsFile)
                        {
                            continue; // Ignore directories
                        }
                        if (string.Compare(zipEntry.Name, fileNameBase, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            zipStream = zf.GetInputStream(zipEntry);
                            break;
                        }
                    }

                    if (zipStream == null)
                    {
                        return null;
                    }
                    try
                    {
                        using (StreamReader sr = new StreamReader(zipStream, Encoding))
                        {
                            bool inSegment = false;
                            for (; ; )
                            {
                                string line = sr.ReadLine();
                                if (line == null)
                                {
                                    break;
                                }

                                if (line.StartsWith("["))
                                {
                                    if (string.Compare(line, segmentStart, StringComparison.OrdinalIgnoreCase) == 0)
                                    {
                                        inSegment = true;
                                    }
                                    else if (string.Compare(line, segmentEnd, StringComparison.OrdinalIgnoreCase) == 0)
                                    {
                                        inSegment = false;
                                    }
                                    continue;
                                }

                                if (!inSegment)
                                {
                                    continue;
                                }
                                string[] lineArray = line.Split(',');
                                if (lineArray.Length > 0)
                                {
                                    lineList.Add(lineArray);
                                }
                            }
                        }
                    }
                    catch (NotImplementedException)
                    {
                        // closing of encrypted stream throws execption
                    }
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    if (zf != null)
                    {
                        zf.IsStreamOwner = true; // Makes close also shut the underlying stream
                        zf.Close(); // Ensure we release resources
                    }
                }
            }
            return lineList;
        }

        public List<string> GetFileList(string fileName)
        {
            string dirName = Path.GetDirectoryName(fileName);
            if (dirName == null)
            {
                return null;
            }
            string fullName = Path.ChangeExtension(fileName, FileExtension);
            if (!File.Exists(fullName))
            {
                string key = Path.GetFileNameWithoutExtension(fileName)?.ToUpperInvariant();
                if (key == null)
                {
                    return null;
                }

                if (!_redirMap.TryGetValue(key, out string mappedName))
                {
                    return null;
                }

                if (string.Compare(mappedName, "EMPTY", StringComparison.OrdinalIgnoreCase) == 0)
                {   // no entry
                    return null;
                }

                fullName = Path.ChangeExtension(mappedName, FileExtension);
                if (fullName == null)
                {
                    return null;
                }
                fullName = Path.Combine(dirName, fullName);

                if (!File.Exists(fullName))
                {
                    return null;
                }
            }

            List<string> includeFiles = new List<string> {fullName};
            if (!GetIncludeFiles(fullName, includeFiles))
            {
                return null;
            }

            return includeFiles;
        }

        public static bool GetIncludeFiles(string fileName, List<string> includeFiles)
        {
            try
            {
                if (!File.Exists(fileName))
                {
                    return false;
                }

                string dir = Path.GetDirectoryName(fileName);
                if (dir == null)
                {
                    return false;
                }

                List<string[]> lineList = ExtractFileSegment(new List<string> { fileName }, "INC");
                if (lineList == null)
                {
                    return false;
                }

                foreach (string[] line in lineList)
                {
                    if (line.Length >= 2)
                    {
                        string file = line[1];
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            string fileNameInc = Path.Combine(dir, Path.ChangeExtension(file, FileExtension));
                            if (File.Exists(fileNameInc) && !includeFiles.Contains(fileNameInc))
                            {
                                includeFiles.Add(fileNameInc);
                                if (!GetIncludeFiles(fileNameInc, includeFiles))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
