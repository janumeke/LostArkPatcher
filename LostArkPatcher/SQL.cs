using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LostArkPatcher
{
    static internal class SQL
    {
        public enum OrderDirection
        {
            Ascending,
            Descending
        }

        static public class Row
        {
            static public string Union(string leftSubSQL, string rightSubSQL)
            {
                return @$"
                    {leftSubSQL}
                    union
                    {rightSubSQL}
                ";
            }

            static public string Count(string table, string? condition = null)
            {
                if (condition is null)
                    return @$"
                        select count(*)
                        from {table}
                    ";
                else
                    return @$"
                        select count(*)
                        from {table}
                        where {condition}
                    ";
            }

            static public string CountQuery(string fromSubSQL)
            {
                return @$"
                    select count(*)
                    from (
                        {fromSubSQL}
                    )
                ";
            }
            
            static public string SumQuery(string fromSubSQL, string column)
            {
                return @$"
                    select sum({column})
                    from (
                        {fromSubSQL}
                    )
                ";
            }

            static public string Order(string table, string byColumn, OrderDirection? direction = null)
            {
                switch (direction)
                {
                    case OrderDirection.Ascending:
                        return @$"
                            select *
                            from {table}
                            order by {byColumn} asc
                        ";

                    case OrderDirection.Descending:
                        return @$"
                            select *
                            from {table}
                            order by {byColumn} desc
                        ";

                    default:
                        return @$"
                            select *
                            from {table}
                            order by {byColumn}
                        ";
                }
            }

            static public string OrderQuery(string subSQL, string byColumn, OrderDirection? direction = null)
            {
                switch(direction)
                {
                    case OrderDirection.Ascending:
                        return @$"
                            select *
                            from (
                                {subSQL}
                            )
                            order by {byColumn} asc
                        ";

                    case OrderDirection.Descending:
                        return @$"
                            select *
                            from (
                                {subSQL}
                            )
                            order by {byColumn} desc
                        ";

                    default:
                        return @$"
                            select *
                            from (
                                {subSQL}
                            )
                            order by {byColumn}
                        ";
                }
            }

            static public string GroupQuery(string fromSubSQL, string byColumn, string? groupCondition = null)
            {
                if(groupCondition is null)
                    return @$"
                        select *
                        from (
                            {fromSubSQL}
                        )
                        group by {byColumn}
                    ";
                else
                    return @$"
                        select *
                        from (
                            {fromSubSQL}
                        )
                        group by {byColumn}
                        having {groupCondition}
                    ";
            }

            static public string Select(string table, string? condition = null, string? columnList = null)
            {
                if (condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from {table}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from {table}
                        where {condition}
                    ";
            }

            static public string SelectQuery(string fromSubSQL, string? columnList = null)
            {
                return @$"
                    select {columnList ?? "*"}
                    from (
                        {fromSubSQL}
                    )
                ";
            }

            static public string SelectInButIn(string column, string inTable, string butTable, string? columnList = null)
            {
                return @$"
                    select {columnList ?? column}
                    from {inTable}
                    where {column} not in (
                        select {column}
                        from {butTable}
                    )
                ";
            }

            static public string SelectInButInQuery(string column, string inTable, string butSubSQL, string? columnList = null)
            {
                return @$"
                    select {columnList ?? column}
                    from {inTable}
                    where {column} not in (
                        select {column}
                        from (
                            {butSubSQL}
                        )
                    )
                ";
            }

            static public string SelectInQueryButInQuery(string column, string inSubSQL, string butSubSQL, string? columnList = null)
            {
                return @$"
                    select {columnList ?? column}
                    from (
                        {inSubSQL}
                    )
                    where {column} not in (
                        select {column}
                        from (
                            {butSubSQL}
                        )
                    )
                ";
            }

            static public string Insert(string columnList, string valueList, string intoTable)
            {
                return @$"
                    insert into {intoTable} ({columnList})
                    values ({valueList})
                ";
            }

            static public string InsertQuery(string fromSubSQL, string columnList, string intoTable)
            {
                return @$"
                    insert into {intoTable}({columnList})
                    select {columnList}
                    from (
                        {fromSubSQL}
                    )
                ";
            }

            static public string Delete(string fromTable, string? condition = null)
            {
                if (condition is null)
                    return @$"
                        delete from {fromTable}
                    ";
                else
                    return @$"
                        delete from {fromTable}
                        where {condition}
                    ";
            }

            static public string DeleteInQuery(string fromTable, string column, string inSubSQL)
            {
                return @$"
                    delete from {fromTable}
                    where {column} in (
                        select {column}
                        from (
                            {inSubSQL}
                        )
                    )
                ";
            }

            static public string Update(string table, string condition, string setList)
            {
                return @$"
                    update {table}
                    set {setList}
                    where {condition}
                ";
            }

            static public string UpdateInQuery(string table, string column, string inSubSQL, string setList)
            {
                return @$"
                    update {table}
                    set {setList}
                    where {column} in (
                        {inSubSQL}
                    )
                ";
            }

            static public string Replace(string table, string columnList, string valueList)
            {
                return @$"
                    replace into {table} ({columnList})
                    values ({valueList})
                ";
            }

            static public string ReplaceQuery(string table, string columnList, string subSQL)
            {
                return @$"
                    replace into {table} ({columnList})
                    {subSQL}
                ";
            }

            /// <remarks>
            /// Use 'left' and 'right' in the <paramref name="on"/> clause to refer to the joined tables.
            /// </remarks>
            static public string LeftJoin(string leftTable, string rightTable, string on, string? condition = null, string? columnList = null)
            {
                if (condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        left join {rightTable} as right
                        on {on}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        left join {rightTable} as right
                        on {on}
                        where {condition}
                    ";
            }

            /// <remarks>
            /// Use 'left' and 'right' in the <paramref name="on"/> clause to refer to the joined tables.
            /// </remarks>
            static public string QueryLeftJoin(string leftSubSQL, string rightTable, string on, string? condition = null, string? columnList = null)
            {
                if (condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from (
                            {leftSubSQL}
                        ) as left
                        left join {rightTable} as right
                        on {on}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from (
                            {leftSubSQL}
                        ) as left
                        left join {rightTable} as right
                        on {on}
                        where {condition}
                    ";
            }

            /// <remarks>
            /// Use 'left' and 'right' in the <paramref name="on"/> clause to refer to the joined tables.
            /// </remarks>
            static public string Join(string leftTable, string rightTable, string on, string? condition = null, string? columnList = null)
            {
                if(condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        join {rightTable} as right
                        on {on}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        join {rightTable} as right
                        on {on}
                        where {condition}
                    ";
            }

            /// <remarks>
            /// Use 'left' and 'right' in the <paramref name="on"/> clause to refer to the joined tables.
            /// </remarks>
            static public string JoinQuery(string leftTable, string rightSubSQL, string on, string? condition = null, string? columnList = null)
            {
                if (condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        join (
                            {rightSubSQL}
                        ) as right
                        on {on}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from {leftTable} as left
                        join (
                            {rightSubSQL}
                        ) as right
                        on {on}
                        where {condition}
                    ";
            }

            /// <remarks>
            /// Use 'left' and 'right' in the <paramref name="on"/> clause to refer to the joined tables.
            /// </remarks>
            static public string QueryJoinQuery(string leftSubSQL, string rightSubSQL, string on, string? condition = null, string? columnList = null)
            {
                if (condition is null)
                    return @$"
                        select {columnList ?? "*"}
                        from (
                            {leftSubSQL}
                        ) as left
                        join (
                            {rightSubSQL}
                        ) as right
                        on {on}
                    ";
                else
                    return @$"
                        select {columnList ?? "*"}
                        from (
                            {leftSubSQL}
                        ) as left
                        join (
                            {rightSubSQL}
                        ) as right
                        on {on}
                        where {condition}
                    ";
            }
        }

        static public class Column
        {
            static public string Type(string table, string column)
            {
                return @$"
                    select type
                    from pragma_table_info('{table}')
                    where name='{column}'
                ";
            }

            static public string Add(string table, string column, string type)
            {
                return @$"
                    alter table {table}
                    add {column} {type}
                ";
            }

            static public string Drop(string table, string column)
            {
                return @$"
                    alter table {table}
                    drop column {column}
                ";
            }
        }

        static public class Table
        {
            static public string Schema(string table)
            {
                return @$"
                    select *
                    from sqlite_schema
                    where type='table' and name='{table}'
                ";
            }

            static public string Create(string table, string schema)
            {
                return $"CREATE TABLE {table} ({schema})";
            }

            static public string CreateTempAsQuery(string asSubSQL, string tempTable)
            {
                return @$"
                    create temporary table {tempTable} as
                    {asSubSQL}
                ";
            }

            static public string Drop(string table)
            {
                return @$"
                    drop table {table}
                ";
            }
        }
    }
}
