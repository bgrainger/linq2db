﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Text;

using LinqToDB.Extensions;

namespace LinqToDB.DataProvider
{
	using Data;
	using Expressions;
	using SqlProvider;

	public class BasicBulkCopy
	{
		public virtual BulkCopyRowsCopied BulkCopy<T>(BulkCopyType bulkCopyType, ITable<T> table, BulkCopyOptions options, IEnumerable<T> source)
		{
			switch (bulkCopyType)
			{
				case BulkCopyType.MultipleRows : return MultipleRowsCopy    (table, options, source);
				case BulkCopyType.RowByRow     : return RowByRowCopy        (table, options, source);
				default                        : return ProviderSpecificCopy(table, options, source);
			}
		}

		protected virtual BulkCopyRowsCopied ProviderSpecificCopy<T>(ITable<T> table, BulkCopyOptions options, IEnumerable<T> source)
		{
			return MultipleRowsCopy(table, options, source);
		}

		protected virtual BulkCopyRowsCopied MultipleRowsCopy<T>(ITable<T> table, BulkCopyOptions options, IEnumerable<T> source)
		{
			return RowByRowCopy(table, options, source);
		}

		protected virtual BulkCopyRowsCopied RowByRowCopy<T>(ITable<T> table, BulkCopyOptions options, IEnumerable<T> source)
		{
			// This limitation could be lifted later for some providers that supports identity insert if we will get such request
			// It will require support from DataConnection.Insert
			if (options.KeepIdentity == true)
				throw new LinqToDBException($"{nameof(BulkCopyOptions)}.{nameof(BulkCopyOptions.KeepIdentity)} = true is not supported by {nameof(BulkCopyType)}.{nameof(BulkCopyType.RowByRow)} mode");

			var rowsCopied = new BulkCopyRowsCopied();

			foreach (var item in source)
			{
				table.DataContext.Insert(item, options.TableName, options.DatabaseName, options.SchemaName);
				rowsCopied.RowsCopied++;

				if (options.NotifyAfter != 0 && options.RowsCopiedCallback != null && rowsCopied.RowsCopied % options.NotifyAfter == 0)
				{
					options.RowsCopiedCallback(rowsCopied);

					if (rowsCopied.Abort)
						break;
				}
			}

			return rowsCopied;
		}

		protected internal static string GetTableName<T>(ISqlBuilder sqlBuilder, BulkCopyOptions options, ITable<T> table)
		{
			var serverName   = options.ServerName   ?? descriptor.ServerName;
			var databaseName = options.DatabaseName ?? table.DatabaseName;
			var schemaName   = options.SchemaName   ?? table.SchemaName;
			var tableName    = options.TableName    ?? table.TableName;

			return sqlBuilder.BuildTableName(
				new StringBuilder(),
				serverName   == null ? null : sqlBuilder.Convert(serverName,   ConvertType.NameToServer).    ToString(),
				databaseName == null ? null : sqlBuilder.Convert(databaseName, ConvertType.NameToDatabase).  ToString(),
				schemaName   == null ? null : sqlBuilder.Convert(schemaName,   ConvertType.NameToSchema).    ToString(),
				tableName    == null ? null : sqlBuilder.Convert(tableName,    ConvertType.NameToQueryTable).ToString())
			.ToString();
		}

		#region ProviderSpecific Support

		protected Func<IDbConnection,int,IDisposable> CreateBulkCopyCreator(
			Type connectionType, Type bulkCopyType, Type bulkCopyOptionType)
		{
			var p1 = Expression.Parameter(typeof(IDbConnection), "pc");
			var p2 = Expression.Parameter(typeof(int),           "po");
			var l  = Expression.Lambda<Func<IDbConnection,int,IDisposable>>(
				Expression.Convert(
					Expression.New(
						bulkCopyType.GetConstructorEx(new[] { connectionType, bulkCopyOptionType }),
						Expression.Convert(p1, connectionType),
						Expression.Convert(p2, bulkCopyOptionType)),
					typeof(IDisposable)),
				p1, p2);

			return l.Compile();
		}

		protected Func<int,string,object> CreateColumnMappingCreator(Type columnMappingType)
		{
			var p1 = Expression.Parameter(typeof(int),    "p1");
			var p2 = Expression.Parameter(typeof(string), "p2");
			var l  = Expression.Lambda<Func<int,string,object>>(
				Expression.Convert(
					Expression.New(
						columnMappingType.GetConstructorEx(new[] { typeof(int), typeof(string) }),
						new Expression[] { p1, p2 }),
					typeof(object)),
				p1, p2);

			return l.Compile();
		}

		protected Action<object,Action<object>> CreateBulkCopySubscriber(object bulkCopy, string eventName)
		{
			var eventInfo   = bulkCopy.GetType().GetEventEx(eventName);
			var handlerType = eventInfo.EventHandlerType;
			var eventParams = handlerType.GetMethodEx("Invoke").GetParameters();

			// Expression<Func<Action<object>,Delegate>> lambda =
			//     actionParameter => Delegate.CreateDelegate(
			//         typeof(int),
			//         (Action<object,DB2RowsCopiedEventArgs>)((o,e) => actionParameter(e)),
			//         "Invoke",
			//         false);

			var actionParameter = Expression.Parameter(typeof(Action<object>), "p1");
			var senderParameter = Expression.Parameter(eventParams[0].ParameterType, eventParams[0].Name);
			var argsParameter   = Expression.Parameter(eventParams[1].ParameterType, eventParams[1].Name);

#if NETSTANDARD1_6
			throw new NotImplementedException("This is not implemented for .Net Core");
#else

			var mi = MemberHelper.MethodOf(() => Delegate.CreateDelegate(typeof(string), (object) null, "", false));

			var lambda = Expression.Lambda<Func<Action<object>,Delegate>>(
				Expression.Call(
					null,
					mi,
					new Expression[]
					{
						Expression.Constant(handlerType, typeof(Type)),
						//Expression.Convert(
							Expression.Lambda(
								Expression.Invoke(actionParameter, new Expression[] { argsParameter }),
								new[] { senderParameter, argsParameter }),
						//	typeof(Action<object, EventArgs>)),
						Expression.Constant("Invoke", typeof(string)),
						Expression.Constant(false, typeof(bool))
					}),
				new[] { actionParameter });

			var dgt = lambda.Compile();

			return (obj,action) => eventInfo.AddEventHandler(obj, dgt(action));
#endif
		}

		protected void TraceAction(DataConnection dataConnection, Func<string> commandText, Func<int> action)
		{
			var now = DateTime.UtcNow;
			var sw  = Stopwatch.StartNew();

			if (DataConnection.TraceSwitch.TraceInfo && dataConnection.OnTraceConnection != null)
			{
				dataConnection.OnTraceConnection(new TraceInfo(TraceInfoStep.BeforeExecute)
				{
					TraceLevel     = TraceLevel.Info,
					DataConnection = dataConnection,
					CommandText    = commandText(),
					StartTime      = now,
				});
			}

			try
			{
				var count = action();

				if (DataConnection.TraceSwitch.TraceInfo && dataConnection.OnTraceConnection != null)
				{
					dataConnection.OnTraceConnection(new TraceInfo(TraceInfoStep.AfterExecute)
					{
						TraceLevel      = TraceLevel.Info,
						DataConnection  = dataConnection,
						CommandText     = commandText(),
						StartTime       = now,
						ExecutionTime   = sw.Elapsed,
						RecordsAffected = count,
					});
				}
			}
			catch (Exception ex)
			{
				if (DataConnection.TraceSwitch.TraceError && dataConnection.OnTraceConnection != null)
				{
					dataConnection.OnTraceConnection(new TraceInfo(TraceInfoStep.Error)
					{
						TraceLevel     = TraceLevel.Error,
						DataConnection = dataConnection,
						CommandText    = commandText(),
						StartTime      = now,
						ExecutionTime  = sw.Elapsed,
						Exception      = ex,
					});
				}

				throw;
			}
		}

		#endregion

		#region MultipleRows Support

		protected BulkCopyRowsCopied MultipleRowsCopy1<T>(
			ITable<T> table, BulkCopyOptions options, IEnumerable<T> source)
		{
			return MultipleRowsCopy1(new MultipleRowsHelper<T>(table, options), source);
		}

		protected BulkCopyRowsCopied MultipleRowsCopy1(MultipleRowsHelper helper, IEnumerable source)
		{
			helper.StringBuilder
				.AppendFormat("INSERT INTO {0}", helper.TableName).AppendLine()
				.Append("(");

			foreach (var column in helper.Columns)
				helper.StringBuilder
					.AppendLine()
					.Append("\t")
					.Append(helper.SqlBuilder.Convert(column.ColumnName, ConvertType.NameToQueryField))
					.Append(",");

			helper.StringBuilder.Length--;
			helper.StringBuilder
				.AppendLine()
				.Append(")");

			helper.StringBuilder
				.AppendLine()
				.Append("VALUES");

			helper.SetHeader();

			foreach (var item in source)
			{
				helper.StringBuilder
					.AppendLine()
					.Append("(");
				helper.BuildColumns(item);
				helper.StringBuilder.Append("),");

				helper.RowsCopied.RowsCopied++;
				helper.CurrentCount++;

				if (helper.CurrentCount >= helper.BatchSize || helper.Parameters.Count > 10000 || helper.StringBuilder.Length > 100000)
				{
					helper.StringBuilder.Length--;
					if (!helper.Execute())
						return helper.RowsCopied;
				}
			}

			if (helper.CurrentCount > 0)
			{
				helper.StringBuilder.Length--;
				helper.Execute();
			}

			return helper.RowsCopied;
		}

		protected virtual BulkCopyRowsCopied MultipleRowsCopy2<T>(
			ITable<T> table, BulkCopyOptions options, IEnumerable<T> source, string from)
		{
			return MultipleRowsCopy2(new MultipleRowsHelper<T>(table, options), source, from);
		}

		protected  BulkCopyRowsCopied MultipleRowsCopy2(
			MultipleRowsHelper helper, IEnumerable source, string from)
		{
			helper.StringBuilder
				.AppendFormat("INSERT INTO {0}", helper.TableName).AppendLine()
				.Append("(");

			foreach (var column in helper.Columns)
				helper.StringBuilder
					.AppendLine()
					.Append("\t")
					.Append(helper.SqlBuilder.Convert(column.ColumnName, ConvertType.NameToQueryField))
					.Append(",");

			helper.StringBuilder.Length--;
			helper.StringBuilder
				.AppendLine()
				.Append(")");

			helper.SetHeader();

			foreach (var item in source)
			{
				helper.StringBuilder
					.AppendLine()
					.Append("SELECT ");
				helper.BuildColumns(item);
				helper.StringBuilder.Append(from);
				helper.StringBuilder.Append(" UNION ALL");

				helper.RowsCopied.RowsCopied++;
				helper.CurrentCount++;

				if (helper.CurrentCount >= helper.BatchSize || helper.Parameters.Count > 10000 || helper.StringBuilder.Length > 100000)
				{
					helper.StringBuilder.Length -= " UNION ALL".Length;
					if (!helper.Execute())
						return helper.RowsCopied;
				}
			}

			if (helper.CurrentCount > 0)
			{
				helper.StringBuilder.Length -= " UNION ALL".Length;
				helper.Execute();
			}

			return helper.RowsCopied;
		}

		protected  BulkCopyRowsCopied MultipleRowsCopy3(
			MultipleRowsHelper helper, BulkCopyOptions options, IEnumerable source, string from)
		{
			helper.StringBuilder
				.AppendFormat("INSERT INTO {0}", helper.TableName).AppendLine()
				.Append("(");

			foreach (var column in helper.Columns)
				helper.StringBuilder
					.AppendLine()
					.Append("\t")
					.Append(helper.SqlBuilder.Convert(column.ColumnName, ConvertType.NameToQueryField))
					.Append(",");

			helper.StringBuilder.Length--;
			helper.StringBuilder
				.AppendLine()
				.AppendLine(")")
				.AppendLine("SELECT * FROM")
				.Append("(");

			helper.SetHeader();

			foreach (var item in source)
			{
				helper.StringBuilder
					.AppendLine()
					.Append("\tSELECT ");
				helper.BuildColumns(item);
				helper.StringBuilder.Append(from);
				helper.StringBuilder.Append(" UNION ALL");

				helper.RowsCopied.RowsCopied++;
				helper.CurrentCount++;

				if (helper.CurrentCount >= helper.BatchSize || helper.Parameters.Count > 10000 || helper.StringBuilder.Length > 100000)
				{
					helper.StringBuilder.Length -= " UNION ALL".Length;
					helper.StringBuilder
						.AppendLine()
						.Append(")");
					if (!helper.Execute())
						return helper.RowsCopied;
				}
			}

			if (helper.CurrentCount > 0)
			{
				helper.StringBuilder.Length -= " UNION ALL".Length;
				helper.StringBuilder
					.AppendLine()
					.Append(")");
				helper.Execute();
			}

			return helper.RowsCopied;
		}

		#endregion
	}
}
