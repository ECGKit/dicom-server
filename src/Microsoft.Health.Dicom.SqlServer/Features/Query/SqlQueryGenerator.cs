﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Health.Dicom.Core.Extensions;
using Microsoft.Health.Dicom.Core.Features.CustomTag;
using Microsoft.Health.Dicom.Core.Features.Query;
using Microsoft.Health.Dicom.Core.Features.Query.Model;
using Microsoft.Health.Dicom.Core.Models;
using Microsoft.Health.Dicom.SqlServer.Features.CustomTag;
using Microsoft.Health.Dicom.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer;
using Microsoft.Health.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Storage;

namespace Microsoft.Health.Dicom.SqlServer.Features.Query
{
    internal class SqlQueryGenerator : QueryFilterConditionVisitor
    {
        private readonly IndentedStringBuilder _stringBuilder;
        private readonly QueryExpression _queryExpression;
        private readonly SqlQueryParameterManager _parameters;
        private const string SqlDateFormat = "yyyy-MM-dd";
        private const string InstanceTableAlias = "i";
        private const string StudyTableAlias = "st";
        private const string SeriesTableAlias = "se";
        private const string CustomTagBigIntTableAlias = "ctbi";
        private const string CustomTagDateTimeTableAlias = "ctdt";
        private const string CustomTagDoubleTableAlias = "ctd";
        private const string CustomTagPersonNameTableAlias = "ctpn";
        private const string CustomTagStringTableAlias = "cts";

        public SqlQueryGenerator(
            IndentedStringBuilder stringBuilder,
            QueryExpression queryExpression,
            SqlQueryParameterManager sqlQueryParameterManager)
        {
            _stringBuilder = stringBuilder;
            _queryExpression = queryExpression;
            _parameters = sqlQueryParameterManager;

            Build();
        }

        private void Build()
        {
            string projectionTableAlias;
            string filterAlias = "f";
            string crossApplyAlias = "x";
            if (_queryExpression.IsInstanceIELevel())
            {
                projectionTableAlias = filterAlias;
            }
            else
            {
                projectionTableAlias = crossApplyAlias;
            }

            AppendSelect(projectionTableAlias);

            // get distinct UIDs based on IE Level
            AppendFilterTable(filterAlias);

            // cross apply with Instance table if needed to find the missing UIDs
            AppendCrossApplyTable(crossApplyAlias, filterAlias);

            AppendOrderBy(projectionTableAlias);

            AppendOptionRecompile();
        }

        private void AppendOptionRecompile()
        {
            _stringBuilder.AppendLine("OPTION(RECOMPILE)");
        }

        private void AppendFilterTable(string filterAlias)
        {
            _stringBuilder.AppendLine("( SELECT ");
            if (_queryExpression.IsInstanceIELevel())
            {
                _stringBuilder.AppendLine(VLatest.Study.StudyInstanceUid, InstanceTableAlias);
                _stringBuilder.Append(",").AppendLine(VLatest.Series.SeriesInstanceUid, InstanceTableAlias);
                _stringBuilder.Append(",").AppendLine(VLatest.Instance.SopInstanceUid, InstanceTableAlias);
                _stringBuilder.Append(",").AppendLine(VLatest.Instance.Watermark, InstanceTableAlias);
            }
            else
            {
                _stringBuilder.AppendLine(VLatest.Study.StudyKey, StudyTableAlias);
                if (_queryExpression.IsSeriesIELevel())
                {
                    _stringBuilder.Append(",").AppendLine(VLatest.Series.SeriesKey, SeriesTableAlias);
                }
            }

            _stringBuilder.AppendLine($"FROM {VLatest.Study.TableName} {StudyTableAlias}");
            if (_queryExpression.IsSeriesIELevel() || _queryExpression.IsInstanceIELevel())
            {
                _stringBuilder.AppendLine($"INNER JOIN {VLatest.Series.TableName} {SeriesTableAlias}");
                _stringBuilder
                    .Append("ON ")
                    .Append(VLatest.Series.StudyKey, SeriesTableAlias)
                    .Append(" = ")
                    .AppendLine(VLatest.Study.StudyKey, StudyTableAlias);
            }

            if (_queryExpression.IsInstanceIELevel())
            {
                _stringBuilder.AppendLine($"INNER JOIN {VLatest.Instance.TableName} {InstanceTableAlias}");
                _stringBuilder
                    .Append("ON ")
                    .Append(VLatest.Instance.SeriesKey, InstanceTableAlias)
                    .Append(" = ")
                    .AppendLine(VLatest.Series.SeriesKey, SeriesTableAlias);
                AppendStatusClause(InstanceTableAlias);
            }

            AppendCustomTagTables();

            _stringBuilder.AppendLine("WHERE 1 = 1");
            using (IndentedStringBuilder.DelimitedScope delimited = _stringBuilder.BeginDelimitedWhereClause())
            {
                AppendFilterClause();
            }

            AppendFilterPaging();

            _stringBuilder.AppendLine($") {filterAlias}");
        }

        private void AppendCustomTagTables()
        {
            Dictionary<CustomTagDataType, CustomTagLevel> queriedCustomTagDataTypesPerLevel = new Dictionary<CustomTagDataType, CustomTagLevel>();
            foreach (CustomTagFilterDetails filterDetails in _queryExpression.QueriedCustomTagFilterDetails)
            {
                CustomTagDataType dataType;
                CustomTagLimit.CustomTagVRAndDataTypeMapping.TryGetValue(filterDetails.Tag.GetDefaultVR().Code, out dataType);
                if (!queriedCustomTagDataTypesPerLevel.TryAdd(dataType, filterDetails.Level) && queriedCustomTagDataTypesPerLevel[dataType] > filterDetails.Level)
                {
                    queriedCustomTagDataTypesPerLevel[dataType] = filterDetails.Level;
                }
            }

            foreach (CustomTagFilterDetails filterDetails in _queryExpression.QueriedCustomTagFilterDetails)
            {
                CustomTagDataType dataType;
                CustomTagLimit.CustomTagVRAndDataTypeMapping.TryGetValue(filterDetails.Tag.GetDefaultVR().Code, out dataType);
                string customTagTableAlias = null;
                _stringBuilder.Append("INNER JOIN ");
                switch (dataType)
                {
                    case CustomTagDataType.StringData:
                        customTagTableAlias = CustomTagStringTableAlias + filterDetails.Key;
                        _stringBuilder.AppendLine($"{VLatest.CustomTagString.TableName} {customTagTableAlias}");

                        break;
                    case CustomTagDataType.LongData:
                        customTagTableAlias = CustomTagBigIntTableAlias + filterDetails.Key;
                        _stringBuilder.AppendLine($"{VLatest.CustomTagBigInt.TableName} {customTagTableAlias}");

                        break;
                    case CustomTagDataType.DoubleData:
                        customTagTableAlias = CustomTagDoubleTableAlias + filterDetails.Key;
                        _stringBuilder.AppendLine($"{VLatest.CustomTagDouble.TableName} {customTagTableAlias}");

                        break;
                    case CustomTagDataType.DateTimeData:
                        customTagTableAlias = CustomTagDateTimeTableAlias + filterDetails.Key;
                        _stringBuilder.AppendLine($"{VLatest.CustomTagDateTime.TableName} {customTagTableAlias}");

                        break;
                    case CustomTagDataType.PersonNameData:
                        customTagTableAlias = CustomTagPersonNameTableAlias + filterDetails.Key;
                        _stringBuilder.AppendLine($"{VLatest.CustomTagPersonName.TableName} {customTagTableAlias}");

                        break;
                }

                _stringBuilder
                    .Append("ON ")
                    .Append($"{customTagTableAlias}.StudyKey")
                    .Append(" = ")
                    .AppendLine(VLatest.Study.StudyKey, StudyTableAlias);

                using (IndentedStringBuilder.DelimitedScope delimited = _stringBuilder.BeginDelimitedOnClause())
                {
                    if ((_queryExpression.IsSeriesIELevel() || _queryExpression.IsInstanceIELevel()) && filterDetails.Level < CustomTagLevel.Study)
                    {
                        _stringBuilder
                            .Append("AND ")
                            .Append("ON ")
                            .Append($"{customTagTableAlias}.SeriesKey")
                            .Append(" = ")
                            .AppendLine(VLatest.Series.SeriesKey, SeriesTableAlias);
                    }

                    if (_queryExpression.IsInstanceIELevel() && filterDetails.Level < CustomTagLevel.Series)
                    {
                        _stringBuilder
                            .Append("AND ")
                            .Append("ON ")
                            .Append($"{customTagTableAlias}.InstanceKey")
                            .Append(" = ")
                            .AppendLine(VLatest.Instance.InstanceKey, InstanceTableAlias);
                    }
                }
            }
        }

        private void AppendCrossApplyTable(string crossApplyAlias, string filterAlias)
        {
            // already have the 3 UID projects needed so skip crossapply for projection
            if (_queryExpression.IsInstanceIELevel())
            {
                return;
            }

            string tableAlias = "a";

            _stringBuilder.AppendLine("CROSS APPLY").AppendLine(" ( ");
            _stringBuilder.AppendLine("SELECT TOP 1");
            _stringBuilder.Append(VLatest.Instance.StudyInstanceUid, tableAlias).AppendLine(",");
            _stringBuilder.Append(VLatest.Instance.SeriesInstanceUid, tableAlias).AppendLine(",");
            _stringBuilder.Append(VLatest.Instance.SopInstanceUid, tableAlias).AppendLine(",");
            _stringBuilder.AppendLine(VLatest.Instance.Watermark, tableAlias);
            _stringBuilder.AppendLine($"FROM {VLatest.Instance.TableName} {tableAlias}");
            _stringBuilder.AppendLine("WHERE 1 = 1");
            using (IndentedStringBuilder.DelimitedScope delimited = _stringBuilder.BeginDelimitedWhereClause())
            {
                _stringBuilder
                    .Append("AND ")
                    .Append(VLatest.Instance.StudyKey, tableAlias)
                    .Append(" = ")
                    .AppendLine(VLatest.Study.StudyKey, filterAlias);

                if (_queryExpression.IsSeriesIELevel())
                {
                    _stringBuilder
                        .Append("AND ")
                        .Append(VLatest.Instance.SeriesKey, tableAlias)
                        .Append(" = ")
                        .AppendLine(VLatest.Series.SeriesKey, filterAlias);
                }

                AppendStatusClause(tableAlias);
                AppendOrderBy(tableAlias);
            }

            _stringBuilder.AppendLine($") {crossApplyAlias}");
        }

        private void AppendSelect(string tableAlias)
        {
            _stringBuilder
                .AppendLine("SELECT ")
                .Append(VLatest.Instance.StudyInstanceUid, tableAlias).AppendLine(",")
                .Append(VLatest.Instance.SeriesInstanceUid, tableAlias).AppendLine(",")
                .Append(VLatest.Instance.SopInstanceUid, tableAlias).AppendLine(",")
                .AppendLine(VLatest.Instance.Watermark, tableAlias)
                .AppendLine("FROM");
        }

        private void AppendStatusClause(string tableAlias)
        {
            byte validStatus = (byte)IndexStatus.Created;
            _stringBuilder
                .Append("AND ")
                .Append(VLatest.Instance.Status, tableAlias)
                .AppendLine($" = {validStatus} ");
        }

        private void AppendFilterClause()
        {
            foreach (var filterCondition in _queryExpression.FilterConditions)
            {
                filterCondition.Accept(this);
            }
        }

        private void AppendFilterPaging()
        {
            BigIntColumn orderColumn = VLatest.Instance.Watermark;
            string tableAlias = InstanceTableAlias;
            if (_queryExpression.IsStudyIELevel())
            {
                orderColumn = VLatest.Study.StudyKey;
                tableAlias = StudyTableAlias;
            }
            else if (_queryExpression.IsSeriesIELevel())
            {
                orderColumn = VLatest.Series.SeriesKey;
                tableAlias = SeriesTableAlias;
            }

            _stringBuilder.Append($"ORDER BY ")
                .Append(orderColumn, tableAlias)
                .AppendLine();
            _stringBuilder.AppendLine($"OFFSET {_queryExpression.Offset} ROWS");
            _stringBuilder.AppendLine($"FETCH NEXT {_queryExpression.EvaluatedLimit} ROWS ONLY");
        }

        private void AppendOrderBy(string tableAlias)
        {
            _stringBuilder
                .Append("ORDER BY ")
                .Append(VLatest.Instance.Watermark, tableAlias)
                .Append(" DESC")
                .AppendLine();
        }

        public override void Visit(StringSingleValueMatchCondition stringSingleValueMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(stringSingleValueMatchCondition.DicomTag);
            var tableAlias = GetTableAlias(dicomTagSqlEntry);
            _stringBuilder
                .Append("AND ");

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, stringSingleValueMatchCondition) ?? tableAlias;

            _stringBuilder
                .Append(dicomTagSqlEntry.SqlColumn, tableAlias)
                .Append("=")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, stringSingleValueMatchCondition.Value));

            _stringBuilder
                .AppendLine();
        }

        public override void Visit(DoubleSingleValueMatchCondition doubleSingleValueMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(doubleSingleValueMatchCondition.DicomTag);
            var tableAlias = GetTableAlias(dicomTagSqlEntry);
            _stringBuilder
                .Append("AND ");

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, doubleSingleValueMatchCondition) ?? tableAlias;

            _stringBuilder
                .Append(dicomTagSqlEntry.SqlColumn, tableAlias)
                .Append("=")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, doubleSingleValueMatchCondition.Value));

            _stringBuilder
                .AppendLine();
        }

        public override void Visit(LongSingleValueMatchCondition longSingleValueMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(longSingleValueMatchCondition.DicomTag);
            var tableAlias = GetTableAlias(dicomTagSqlEntry);
            _stringBuilder
                .Append("AND ");

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, longSingleValueMatchCondition) ?? tableAlias;

            _stringBuilder
                .Append(dicomTagSqlEntry.SqlColumn, tableAlias)
                .Append("=")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, longSingleValueMatchCondition.Value));

            _stringBuilder
                .AppendLine();
        }

        public override void Visit(DateRangeValueMatchCondition rangeValueMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(rangeValueMatchCondition.DicomTag);
            var tableAlias = GetTableAlias(dicomTagSqlEntry);
            _stringBuilder
                .Append("AND ");

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, rangeValueMatchCondition) ?? tableAlias;

            _stringBuilder
                .Append(dicomTagSqlEntry.SqlColumn, tableAlias).Append(" BETWEEN ")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, rangeValueMatchCondition.Minimum.ToString(SqlDateFormat)))
                .Append(" AND ")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, rangeValueMatchCondition.Maximum.ToString(SqlDateFormat)));

            _stringBuilder
                .AppendLine();
        }

        public override void Visit(DateSingleValueMatchCondition dateSingleValueMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(dateSingleValueMatchCondition.DicomTag);
            var tableAlias = GetTableAlias(dicomTagSqlEntry);
            _stringBuilder
                .Append("AND ");

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, dateSingleValueMatchCondition) ?? tableAlias;

            _stringBuilder
                .Append(dicomTagSqlEntry.SqlColumn, tableAlias)
                .Append("=")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, dateSingleValueMatchCondition.Value.ToString(SqlDateFormat)));

            _stringBuilder
                .AppendLine();
        }

        public override void Visit(PersonNameFuzzyMatchCondition fuzzyMatchCondition)
        {
            var dicomTagSqlEntry = DicomTagSqlEntry.GetDicomTagSqlEntry(fuzzyMatchCondition.DicomTag);
            char[] delimiterChars = { ' ' };
            string[] words = fuzzyMatchCondition.Value.Split(delimiterChars, System.StringSplitOptions.RemoveEmptyEntries);

            var fuzzyMatchString = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
            var tableAlias = GetTableAlias(dicomTagSqlEntry);

            tableAlias = AppendCustomTagKeyFilter(dicomTagSqlEntry, tableAlias, fuzzyMatchCondition) ?? tableAlias;
            _stringBuilder
                .Append("AND CONTAINS(")
                .Append(dicomTagSqlEntry.FullTextIndexColumnName)
                .Append(", ")
                .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlColumn, fuzzyMatchString))
                .Append(")")
                .AppendLine();
        }

        private string AppendCustomTagKeyFilter(DicomTagSqlEntry dicomTagSqlEntry, string tableAlias, QueryFilterCondition filterCondition)
        {
            if (dicomTagSqlEntry.IsCustomTag)
            {
                tableAlias += filterCondition.CustomTagFilterDetails.Key;
                _stringBuilder
                    .Append(dicomTagSqlEntry.SqlKeyColumn, tableAlias)
                    .Append("=")
                    .Append(_parameters.AddParameter(dicomTagSqlEntry.SqlKeyColumn, filterCondition.CustomTagFilterDetails.Key))
                    .AppendLine()
                    .Append("AND ");
                return tableAlias;
            }

            return null;
        }

        private static string GetTableAlias(DicomTagSqlEntry sqlEntry)
        {
            switch (sqlEntry.SqlTableType)
            {
                case SqlTableType.InstanceTable: return InstanceTableAlias;
                case SqlTableType.StudyTable: return StudyTableAlias;
                case SqlTableType.SeriesTable: return SeriesTableAlias;
                case SqlTableType.CustomTagBigIntTable: return CustomTagBigIntTableAlias;
                case SqlTableType.CustomTagDateTimeTable: return CustomTagDateTimeTableAlias;
                case SqlTableType.CustomTagDoubleTable: return CustomTagDoubleTableAlias;
                case SqlTableType.CustomTagPersonNameTable: return CustomTagPersonNameTableAlias;
                case SqlTableType.CustomTagStringTable: return CustomTagStringTableAlias;
            }

            Debug.Fail("Invalid table type");
            return null;
        }
    }
}
