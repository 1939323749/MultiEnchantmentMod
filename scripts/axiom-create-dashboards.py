#!/usr/bin/env python3
"""Create the standard MultiEnchantmentMod telemetry dashboards in Axiom.

Idempotent-ish: pass a stable `uid` per dashboard with overwrite=True so re-running
updates in place instead of creating duplicates. Reads creds from .axiom-query-token
(the query/management token, NOT the embedded ingest token).
"""
import json, os, re, urllib.request, urllib.error

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
creds = {}
with open(os.path.join(ROOT, ".axiom-query-token"), encoding="utf-8", errors="ignore") as f:
    for line in f:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            creds[k.strip()] = v.strip()
TOKEN = creds["AXIOM_QUERY_TOKEN"]
DOMAIN = creds.get("AXIOM_DOMAIN", "https://api.axiom.co")
DS = creds.get("AXIOM_DATASET", "multienchantmentmod")
BASE = f"['{DS}']"
REAL = "isnull(source)"  # drop manual smoke-test rows


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(DOMAIN + path, data=data, method=method,
                                 headers={"Authorization": f"Bearer {TOKEN}",
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read() or "null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:400]


def chart(cid, ctype, name, apl):
    return {"id": cid, "type": ctype, "name": name, "query": {"apl": f"{BASE} | {apl}"}}


def L(i, x, y, w, h):
    return {"i": i, "x": x, "y": y, "w": w, "h": h}


DASHBOARDS = [
    {
        "uid": "mem-overview",
        "name": "MultiEnchantmentMod · Overview",
        "description": "Adoption and event volume for the mod's telemetry (Axiom).",
        "charts": [
            chart("players", "Statistic", "Distinct players", f"where {REAL} | summarize dcount(distinct_id)"),
            chart("combats", "Statistic", "Combats cleared", f"where event == 'combat_completed' and {REAL} | summarize count()"),
            chart("runs", "Statistic", "Runs", f"where event == 'run_ended' and {REAL} | summarize count()"),
            chart("players_ts", "TimeSeries", "Distinct players over time", f"where {REAL} | summarize dcount(distinct_id) by bin_auto(_time)"),
            chart("flow_ts", "TimeSeries", "Combats & runs over time", f"where {REAL} | summarize combats=countif(event == 'combat_completed'), runs=countif(event == 'run_ended') by bin_auto(_time)"),
            chart("events_pie", "Pie", "Events by type", f"where {REAL} | summarize count() by event"),
        ],
        "layout": [L("players", 0, 0, 4, 3), L("combats", 4, 0, 4, 3), L("runs", 8, 0, 4, 3),
                   L("players_ts", 0, 3, 12, 4),
                   L("flow_ts", 0, 7, 6, 4), L("events_pie", 6, 7, 6, 4)],
    },
    {
        "uid": "mem-gameplay",
        "name": "MultiEnchantmentMod · Gameplay",
        "description": "Characters, run outcomes and multiplayer mix.",
        "charts": [
            chart("chars", "Table", "Top characters by combats", f"where event == 'combat_completed' and {REAL} | summarize combats=count(), players=dcount(distinct_id) by character | sort by combats desc"),
            chart("outcomes", "Pie", "Run outcomes", f"where event == 'run_ended' and {REAL} | summarize count() by outcome"),
            chart("mp", "Pie", "Single vs multiplayer (combats)", f"where event == 'combat_completed' and {REAL} | summarize count() by is_multiplayer"),
            chart("asc", "Table", "Ascension distribution (runs)", f"where event == 'run_ended' and {REAL} | summarize runs=count() by ascension | sort by ascension asc"),
        ],
        "layout": [L("chars", 0, 0, 6, 6), L("outcomes", 6, 0, 6, 3), L("mp", 6, 3, 6, 3),
                   L("asc", 0, 6, 12, 4)],
    },
    {
        "uid": "mem-enchantments",
        "name": "MultiEnchantmentMod · Enchantments",
        "description": "The mod's core signal: how much and how deep cards get enchanted.",
        "charts": [
            chart("total", "Statistic", "Total enchant applications", f"where event == 'combat_completed' and {REAL} | summarize sum(total_enchant_applications)"),
            chart("avg", "Statistic", "Avg per combat", f"where event == 'combat_completed' and {REAL} | summarize avg_apps=round(avg(total_enchant_applications), 2)"),
            chart("max", "Statistic", "Max on one card", f"where event == 'combat_completed' and {REAL} | summarize max(max_enchantments_on_single_card)"),
            chart("active", "Statistic", "Combats with enchanting", f"where event == 'combat_completed' and {REAL} | summarize countif(total_enchant_applications > 0)"),
            chart("apps_ts", "TimeSeries", "Enchant applications over time", f"where event == 'combat_completed' and {REAL} | summarize sum(total_enchant_applications) by bin_auto(_time)"),
            chart("dist", "Table", "Enchantments per combat", f"where event == 'combat_completed' and {REAL} | summarize combats=count() by total_enchant_applications | sort by total_enchant_applications asc"),
            chart("platform", "Pie", "Players by platform", f"where isnotnull(os_platform) and {REAL} | summarize dcount(distinct_id) by os_platform"),
        ],
        "layout": [L("total", 0, 0, 3, 3), L("avg", 3, 0, 3, 3), L("max", 6, 0, 3, 3), L("active", 9, 0, 3, 3),
                   L("apps_ts", 0, 3, 12, 4),
                   L("dist", 0, 7, 6, 5), L("platform", 6, 7, 6, 5)],
    },
    {
        "uid": "mem-trends",
        "name": "MultiEnchantmentMod · Trends",
        "description": "Daily adoption, outcomes and stability — watch the plateau and post-release regressions.",
        "charts": [
            chart("players", "Statistic", "Players", f"where {REAL} | summarize dcount(distinct_id)"),
            chart("runs", "Statistic", "Runs", f"where event == 'run_ended' and {REAL} | summarize count()"),
            chart("crashes", "Statistic", "Crashes", f"where event == 'mod_crash' and {REAL} | summarize count()"),
            chart("dau", "TimeSeries", "Daily active players", f"where {REAL} | summarize players=dcount(distinct_id) by bin(_time, 1d)"),
            chart("druns", "TimeSeries", "Daily runs", f"where event == 'run_ended' and {REAL} | summarize runs=count() by bin(_time, 1d)"),
            chart("dmp", "TimeSeries", "Daily multiplayer players", f"where {REAL} | summarize mp_players=dcountif(distinct_id, is_multiplayer == true) by bin(_time, 1d)"),
            chart("dwl", "TimeSeries", "Daily victories vs deaths", f"where event == 'run_ended' and {REAL} | summarize victories=countif(outcome == 'victory'), deaths=countif(outcome == 'death') by bin(_time, 1d)"),
        ],
        "layout": [L("players", 0, 0, 4, 3), L("runs", 4, 0, 4, 3), L("crashes", 8, 0, 4, 3),
                   L("dau", 0, 3, 12, 4),
                   L("druns", 0, 7, 6, 4), L("dmp", 6, 7, 6, 4),
                   L("dwl", 0, 11, 12, 4)],
    },
    {
        "uid": "mem-versions",
        "name": "MultiEnchantmentMod · Versions",
        "description": "Version adoption and update propagation — each player counted on their latest build.",
        "charts": [
            chart("builds", "Statistic", "Builds in the wild", f"where isnotnull(mod_version) and {REAL} | summarize dcount(mod_version)"),
            chart("ver_pie", "Pie", "Current version share", f"where isnotnull(mod_version) and {REAL} | summarize arg_max(_time, mod_version) by distinct_id | summarize players=count() by mod_version"),
            chart("ver_table", "Table", "Players by version (latest per player)", f"where isnotnull(mod_version) and {REAL} | summarize arg_max(_time, mod_version) by distinct_id | summarize players=count() by mod_version | sort by players desc"),
            chart("ver_ts", "TimeSeries", "Daily active players by version", f"where isnotnull(mod_version) and {REAL} | summarize players=dcount(distinct_id) by bin(_time, 1d), mod_version"),
        ],
        "layout": [L("builds", 0, 0, 4, 3), L("ver_pie", 4, 0, 8, 5),
                   L("ver_table", 0, 3, 4, 5),
                   L("ver_ts", 0, 8, 12, 5)],
    },
]


def main():
    # Clean up probe dashboards from earlier exploration.
    status, listing = api("GET", "/v2/dashboards")
    if isinstance(listing, list):
        for d in listing:
            doc = d.get("dashboard", d)
            name = doc.get("name", "")
            uid = doc.get("uid", "")
            if "probe" in name.lower() and uid:
                print("delete probe:", name, api("DELETE", f"/v2/dashboards/uid/{uid}")[0])

    for d in DASHBOARDS:
        body = {
            "uid": d["uid"],
            "overwrite": True,
            "dashboard": {
                "name": d["name"],
                "description": d["description"],
                "owner": "X-AXIOM-EVERYONE",
                "schemaVersion": 2,
                "refreshTime": 60,
                "timeWindowStart": "qr-now-30d",
                "timeWindowEnd": "qr-now",
                "charts": d["charts"],
                "layout": d["layout"],
            },
        }
        code, resp = api("POST", "/v2/dashboards", body)
        ok = code in (200, 201)
        print(f"{'OK ' if ok else 'ERR'} {code}  {d['name']}  uid={d['uid']}")
        if not ok:
            print("   ", resp)


if __name__ == "__main__":
    main()
