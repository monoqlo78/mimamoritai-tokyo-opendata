#!/usr/bin/env python3
"""見守り隊 ― Power BI セマンティックモデル（TMDL）の生成。

fabric-app/scripts/semantic-model-views.sql で作ったビューの上に、
DirectQuery のセマンティックモデルを組み立てて TMDL として書き出す。

出力先は fabric-app/semantic-model/。deploy-semantic-model.ps1 がこれを
Fabric へ送る。手で TMDL を書かないのは、列の型や順序をビューの定義と
ずれさせないため。ビューを直したらこのスクリプトを流し直す。

使い方:
    python scripts/gen-semantic-model.py
"""

from __future__ import annotations

import pathlib
import uuid

# 決定論的に lineageTag を振るための名前空間。実行のたびに GUID が変わると
# 差分がノイズだらけになり、レビューできなくなる。
NS = uuid.UUID("6f2b1c94-8d5a-4f31-9c77-2a3e5b8d1f40")


def tag(*parts: str) -> str:
    return str(uuid.uuid5(NS, "/".join(parts)))


# SQL の型 -> TMDL の型。ビューは TRY_CONVERT で型を戻してあるので、
# ここに現れる型はすべて意図したもの。
TYPE_MAP = {
    "varchar": "string",
    "nvarchar": "string",
    "char": "string",
    "int": "int64",
    "bigint": "int64",
    "decimal": "decimal",
    "datetime2": "dateTime",
    "date": "dateTime",
    "bit": "boolean",
}

# 既定の集計。キーや順序列を合計してしまうと意味のない数字が出るので、
# 「足して意味がある列」だけ sum にする。
SUM_COLUMNS = {
    "イベント数", "ON回数", "電力量Wh",
    "呼び出し回数", "成功回数", "失敗回数",
    "スコア", "失敗フラグ",
    "期間内アラート数", "期間内通知失敗数",
    "家族人数", "見守り対象人数", "機器台数", "LINE通知先数",
    "本日電力量Wh", "平常時電力量Wh",
}
AVG_COLUMNS = {
    "気温C", "最低気温C", "最高気温C", "湿度Pct", "暑さ指数WBGT",
    "平均応答ms", "熱中症警戒度", "低体温警戒度", "観測件数",
}

TABLES: dict[str, list[tuple[str, str]]] = {
    "v_Household": [
        ("HouseholdKey", "varchar"), ("HouseholdId", "varchar"), ("世帯名", "varchar"),
        ("データ種別", "varchar"), ("家族人数", "int"), ("見守り対象人数", "int"),
        ("機器台数", "int"), ("最終イベント日時UTC", "datetime2"),
        ("SwitchBot接続状態", "varchar"), ("SwitchBotエラー", "varchar"),
        ("LINE通知先数", "int"), ("期間内アラート数", "int"), ("期間内通知失敗数", "int"),
        ("直近リスク", "varchar"), ("直近リスク順", "int"), ("要対応", "bit"),
        ("本日電力量Wh", "decimal"), ("平常時電力量Wh", "decimal"),
        ("電力傾向", "varchar"), ("取得日時", "datetime2"),
    ],
    "v_Alert": [
        ("AlertKey", "varchar"), ("HouseholdId", "varchar"), ("世帯名", "varchar"),
        ("リスク", "varchar"), ("リスク順", "int"), ("スコア", "int"),
        ("理由", "varchar"), ("送信成功", "bit"), ("失敗フラグ", "int"),
        ("エラー", "varchar"), ("送信日時", "datetime2"), ("送信日", "date"),
    ],
    "v_ActivityHourly": [
        ("ActivityKey", "varchar"), ("HouseholdId", "varchar"), ("世帯名", "varchar"),
        ("DeviceId", "varchar"), ("機器名", "varchar"), ("機器種別", "varchar"),
        ("取得元", "varchar"), ("時刻", "datetime2"), ("日付", "date"), ("時", "int"),
        ("イベント数", "int"), ("ON回数", "int"), ("電力量Wh", "decimal"),
    ],
    "v_OutdoorHourly": [
        ("OutdoorKey", "varchar"), ("観測点コード", "varchar"), ("地域名", "varchar"),
        ("時刻", "datetime2"), ("日付", "date"), ("時", "int"),
        ("気温C", "decimal"), ("最低気温C", "decimal"), ("最高気温C", "decimal"),
        ("湿度Pct", "decimal"), ("暑さ指数WBGT", "decimal"),
        ("熱中症警戒度", "int"), ("低体温警戒度", "int"), ("観測件数", "int"),
    ],
    "v_AiRouterCall": [
        ("AiCallKey", "varchar"), ("用途", "varchar"), ("ルーター", "varchar"),
        ("実際のモデル", "varchar"), ("呼び出し回数", "int"), ("成功回数", "int"),
        ("失敗回数", "int"), ("平均応答ms", "decimal"),
        ("最終呼び出し日時", "datetime2"), ("最終呼び出し日", "date"),
    ],
    "v_Date": [
        ("日付", "date"), ("年", "int"), ("月", "int"), ("日", "int"),
        ("曜日番号", "int"), ("年月", "char"),
    ],
}

# 「並べ替え用の数値列」を対応する表示列に結びつける。これをしないと
# 凡例が High / Low / Medium という五十音順になり、危険度の順序が読めない。
SORT_BY = {
    ("v_Household", "直近リスク"): "直近リスク順",
    ("v_Alert", "リスク"): "リスク順",
}

# 画面に出す必要のない内部列。消さずに隠すのは、リレーションや並べ替えで
# 使っているため。
HIDDEN = {
    ("v_Household", "HouseholdKey"), ("v_Household", "直近リスク順"),
    ("v_Alert", "AlertKey"), ("v_Alert", "リスク順"),
    ("v_ActivityHourly", "ActivityKey"), ("v_OutdoorHourly", "OutdoorKey"),
    ("v_AiRouterCall", "AiCallKey"),
}

# 日付テーブルへのリレーション。屋外（気温）と屋内（電力）を同じ軸に
# 並べるためのもので、このモデルの中心。
RELATIONSHIPS = [
    ("v_ActivityHourly", "日付", "v_Date", "日付"),
    ("v_OutdoorHourly", "日付", "v_Date", "日付"),
    ("v_Alert", "送信日", "v_Date", "日付"),
]

MEASURES = [
    ("v_ActivityHourly", "総電力量Wh", "SUM('v_ActivityHourly'[電力量Wh])", "#,0.0"),
    ("v_ActivityHourly", "計測済み時間数",
     "COUNTROWS(FILTER('v_ActivityHourly', NOT ISBLANK('v_ActivityHourly'[電力量Wh])))", "#,0"),
    ("v_OutdoorHourly", "平均気温C", "AVERAGE('v_OutdoorHourly'[気温C])", "#,0.0"),
    ("v_OutdoorHourly", "最高暑さ指数", "MAX('v_OutdoorHourly'[暑さ指数WBGT])", "#,0.0"),
    ("v_Alert", "通知件数", "COUNTROWS('v_Alert')", "#,0"),
    ("v_Alert", "通知失敗件数", "SUM('v_Alert'[失敗フラグ])", "#,0"),
    ("v_Alert", "通知失敗率",
     "DIVIDE(SUM('v_Alert'[失敗フラグ]), COUNTROWS('v_Alert'))", "0.0%"),
    ("v_AiRouterCall", "AI呼び出し回数", "SUM('v_AiRouterCall'[呼び出し回数])", "#,0"),
    ("v_AiRouterCall", "AI成功率",
     "DIVIDE(SUM('v_AiRouterCall'[成功回数]), SUM('v_AiRouterCall'[呼び出し回数]))", "0.0%"),
]


def summarize_by(table: str, col: str, sql_type: str) -> str:
    if (table, col) in HIDDEN:
        return "none"
    if col in SUM_COLUMNS:
        return "sum"
    if col in AVG_COLUMNS:
        return "average"
    return "none"


def render_table(name: str, cols: list[tuple[str, str]], server: str, database: str) -> str:
    lines = [f"table {name}", f"\tlineageTag: {tag('table', name)}", ""]

    if name == "v_Date":
        # Power BI に日付テーブルだと知らせる。これがないと、屋外と屋内の
        # 二つのファクトを同じ時間軸に並べられない。
        lines.insert(2, "\tdataCategory: Time")

    for col, sql_type in cols:
        tmdl_type = TYPE_MAP[sql_type]
        lines.append(f"\tcolumn '{col}'")
        lines.append(f"\t\tdataType: {tmdl_type}")
        if name == "v_Date" and col == "日付":
            lines.append("\t\tisKey")
        if (name, col) in HIDDEN:
            lines.append("\t\tisHidden")
        if tmdl_type == "dateTime":
            lines.append('\t\tformatString: yyyy/MM/dd')
        lines.append(f"\t\tlineageTag: {tag('column', name, col)}")
        lines.append(f"\t\tsummarizeBy: {summarize_by(name, col, sql_type)}")
        lines.append(f"\t\tsourceColumn: {col}")
        sort_col = SORT_BY.get((name, col))
        if sort_col:
            lines.append(f"\t\tsortByColumn: '{sort_col}'")
        lines.append("")
        lines.append("\t\tannotation SummarizationSetBy = Automatic")
        lines.append("")

    for m_table, m_name, m_expr, m_fmt in MEASURES:
        if m_table != name:
            continue
        lines.append(f"\tmeasure '{m_name}' = {m_expr}")
        lines.append(f"\t\tformatString: {m_fmt}")
        lines.append(f"\t\tlineageTag: {tag('measure', name, m_name)}")
        lines.append("")

    # DirectQuery。取り込み（Import）にしないのは、コンソールが数分おきに
    # 更新するデータを見るのに、再取り込みの待ち時間を挟みたくないため。
    lines.append(f"\tpartition {name} = m")
    lines.append("\t\tmode: directQuery")
    lines.append("\t\tsource =")
    lines.append("\t\t\t\tlet")
    lines.append(f'\t\t\t\t    Source = Sql.Database("{server}", "{database}"),')
    lines.append(f'\t\t\t\t    Data = Source{{[Schema="dbo",Item="{name}"]}}[Data]')
    lines.append("\t\t\t\tin")
    lines.append("\t\t\t\t    Data")
    lines.append("")
    lines.append("\tannotation PBI_ResultType = Table")
    lines.append("")
    return "\n".join(lines)


def render_model() -> str:
    lines = [
        "model Model",
        "\tculture: ja-JP",
        "\tdefaultPowerBIDataSourceVersion: powerBI_V3",
        "\tdiscourageImplicitMeasures",
        "\tsourceQueryCulture: ja-JP",
        "",
        "\tannotation PBI_QueryOrder = [" + ",".join(f'"{t}"' for t in TABLES) + "]",
        "",
    ]
    for t in TABLES:
        lines.append(f"ref table {t}")
    lines.append("")
    return "\n".join(lines)


def render_relationships() -> str:
    lines = []
    for from_t, from_c, to_t, to_c in RELATIONSHIPS:
        lines.append(f"relationship {tag('rel', from_t, from_c, to_t, to_c)}")
        lines.append(f"\tfromColumn: {from_t}.'{from_c}'")
        lines.append(f"\ttoColumn: {to_t}.'{to_c}'")
        lines.append("")
    return "\n".join(lines)


def main() -> None:
    root = pathlib.Path(__file__).resolve().parents[1]
    server = "hulcbwod5oduzik4a7tujiuoxu-mcfkjys7bmpufen3kgrt7zjixq.datawarehouse.fabric.microsoft.com"
    database = "mimamoritai-admin"

    out = root / "fabric-app" / "semantic-model"
    (out / "definition" / "tables").mkdir(parents=True, exist_ok=True)

    files: dict[str, str] = {
        "definition.pbism": '{\n  "version": "4.0",\n  "settings": {}\n}\n',
        "definition/database.tmdl": "database\n\tcompatibilityLevel: 1604\n",
        "definition/model.tmdl": render_model(),
        "definition/relationships.tmdl": render_relationships(),
    }
    for name, cols in TABLES.items():
        files[f"definition/tables/{name}.tmdl"] = render_table(name, cols, server, database)

    for rel_path, text in files.items():
        p = out / rel_path
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8", newline="\n")
        print(f"  {rel_path}  ({len(text)} chars)")

    print(f"\n{len(files)} files -> {out}")


if __name__ == "__main__":
    main()
