#!/usr/bin/env python3
"""性能公称値（architecture.md §9 M-6）と流量制御の既定閾値（M-4）の調査結果を判定・集計する。

.github/workflows/perf-nominal-survey.yml の各ジョブ（ランナー個体 1 台 = 標本 1 つ）が残した
Yagura.Bench の結果 JSON を読み、1 段ごとの合否、個体ごとの値 R_i、版ごとの分布、下側限界 L と
公称値 X、検証フェーズの全数合格、流量制御の発火の有無を出す。

算出方法（下の「算出方法の定数」と judge_step の合格条件）は、標本を取る前に固定しておくための
ものである。標本を見てから閾値や順位を動かすと、都合のよい数字を選べてしまい、公称値が持つはずの
保証（95% 信頼で個体の 90% 以上が L 以上）が意味を失う。変更は必ず PR で行い、変更の理由と、
その時点までに取った標本との関係を PR に残すこと。

1 段の合否はこのスクリプトだけが決める。workflow の梯子（2 段連続不合格で打ち切り）も
judge サブコマンドを呼んでおり、合格の定義を 2 か所に持たない。

ランナーの Python へ追加導入をしないため、標準ライブラリだけを使う。

使い方:
  aggregate-nominal-survey.py judge <結果JSON> [--flow-control off|on|any]
  aggregate-nominal-survey.py aggregate <artifact ディレクトリ> --phase <phase>
      [--out-json PATH] [--out-md PATH] [--nominal N1=4500,N2=...] [--strict]
"""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import sys
from fractions import Fraction
from pathlib import Path
from typing import Any, Iterable

# ---------------------------------------------------------------------------
# 算出方法の定数（変更は PR で行う。docstring 参照）
# ---------------------------------------------------------------------------

# 版あたりの標本数 n と、下側限界 L に採る順位（小さい方から何番目か）。
# n = 29 の最小値は、母集団の 10% 点を 1 - 0.9**29 ≈ 0.953 の確率で下回る（Wilks の順序統計量）。
# つまり「95% 信頼で個体の 90% 以上が L 以上」。順位を 2 番目にするには n = 46 が要る。
SAMPLES_PER_VERSION = 29
LOWER_BOUND_RANK = 1

# 公称値 X = floor(L × NOMINAL_FACTOR / NOMINAL_STEP) × NOMINAL_STEP。
# 0.8 はランナー群の構成変化（CPU 世代・イメージ更新）で下側限界が動く分の余裕。
# 500 msg/s 刻みにするのは、測定の分解能より細かい数字を表明しないため。
NOMINAL_FACTOR = Fraction(4, 5)
NOMINAL_STEP = 500

# 公称値を表明する版。両方で測り、小さい方の L から X を出す（選ばれなかった版を残さない）。
REQUIRED_VERSIONS = ("windows-2022", "windows-2025")

# 1 段の合格条件のうち、送出側の条件。送信元が目標レートを出せていない段は、受信側が
# 受け切れたかどうかを語れないため不合格にする。
REQUIRED_SEND_RATIO = 0.99

# 確定時の検証: X で K ジョブを走らせ全数合格を要求する。全数合格なら、個体ごとの
# 不合格率の 95% 上側信頼限界は 1 - 0.05**(1/30) ≈ 9.5% になる。
VERIFY_JOBS_REQUIRED = 30
VERIFY_MIN_DURATION_SECONDS = 300
LONG_RUN_DURATION_SECONDS = 1800

# 余裕率 h = L_new / X - 1 の区分。h < 0.10 は基準値の引き下げをリリース判定と同じ重さで扱い、
# h < 0 は公称値割れ（性能の修正か、supersession 手続きによる引き下げが要る）。
MARGIN_RELEASE_GATE = 0.10
MARGIN_BREACH = 0.0

# ---------------------------------------------------------------------------
# 系列の定義
# ---------------------------------------------------------------------------

# 系列 = 1 本の梯子。公称値を表明するのは N1〜N3 のみ。SQL Server は参考値。
SERIES: dict[str, dict[str, Any]] = {
    "N1": {"scenario": "SustainedZeroDrop", "transport": "udp", "provider": "Sqlite",
           "label": "UDP 持続受信（損失 0・退避 0）", "unit": "msg/s", "nominal": True},
    "N2": {"scenario": "SustainedZeroDrop", "transport": "tcp", "provider": "Sqlite",
           "label": "TCP 持続受信（目標の 99% 以上・退避 0）", "unit": "msg/s", "nominal": True},
    "N3": {"scenario": "ProviderWriteCeiling", "transport": "tcp", "provider": "Sqlite",
           "label": "SQLite 書き込み上限（退避 0）", "unit": "msg/s", "nominal": True},
    "REF-SQLSERVER": {"scenario": "ProviderWriteCeiling", "transport": "tcp", "provider": "SqlServer",
                      "label": "SQL Server 書き込み上限（参考値）", "unit": "msg/s", "nominal": False},
    "BURST": {"scenario": "BurstQ1Drop", "transport": "udp", "provider": "Sqlite",
              "label": "無損失で吸収できる最大バースト B_q1", "unit": "件", "nominal": False},
}

SUSTAINED_SERIES = ("N1", "N2", "N3", "REF-SQLSERVER")
IDENTITY_FILE_NAME = "runner-identity.json"
LADDER_LOG_FILE_NAME = "ladder-log.json"

# Windows のビルド番号 → ランナーの版。ランナーのラベルが記録されていない JSON
# （ローカル実行・旧形式）を版へ振り分けるために使う。
BUILD_TO_VERSION = {"17763": "windows-2019", "20348": "windows-2022", "26100": "windows-2025"}

PHASES = ("explore", "sample", "verify", "flowcontrol")


# ---------------------------------------------------------------------------
# 入力の読み取り
# ---------------------------------------------------------------------------


def load_json(path: Path) -> Any:
    # PowerShell 5.1 の Set-Content は BOM 付きで書くため utf-8-sig で読む。
    with path.open(encoding="utf-8-sig") as f:
        return json.load(f)


def is_scenario_report(document: Any) -> bool:
    return isinstance(document, dict) and "ScenarioName" in document and "Reconciliation" in document


_TIMESPAN = re.compile(r"^(?:(?P<days>\d+)\.)?(?P<h>\d+):(?P<m>\d+):(?P<s>\d+(?:\.\d+)?)$")


def parse_timespan(value: Any) -> float:
    """.NET の TimeSpan 既定書式（[d.]hh:mm:ss[.fffffff]）を秒へ変換する。"""
    if isinstance(value, (int, float)):
        return float(value)
    match = _TIMESPAN.match(str(value).strip())
    if not match:
        raise ValueError(f"TimeSpan として解釈できない: {value!r}")
    days = int(match.group("days") or 0)
    return days * 86400 + int(match.group("h")) * 3600 + int(match.group("m")) * 60 + float(match.group("s"))


def transport_of(report: dict[str, Any]) -> str:
    # 列挙値は既定のシリアライザで数値になる（0 = Udp、1 = Tcp）。文字列で書かれた JSON も受ける。
    raw = report.get("LoadGeneratorOptions", {}).get("Transport", 0)
    if isinstance(raw, str):
        return raw.strip().lower()
    return {0: "udp", 1: "tcp"}.get(raw, str(raw))


def provider_of(report: dict[str, Any]) -> str:
    conditions = report.get("Conditions")
    if isinstance(conditions, dict) and conditions.get("Provider"):
        return str(conditions["Provider"])
    metric = report.get("AdditionalMetrics", {}).get("provider")
    return str(metric) if metric else "Sqlite"


def classify(report: dict[str, Any]) -> str | None:
    scenario = report.get("ScenarioName")
    transport = transport_of(report)
    provider = provider_of(report)
    for key, spec in SERIES.items():
        if spec["scenario"] == scenario and spec["transport"] == transport and spec["provider"] == provider:
            return key
    return None


def version_from_build(os_description: str | None) -> str:
    match = re.search(r"10\.0\.(\d+)", os_description or "")
    if not match:
        return "unknown"
    return BUILD_TO_VERSION.get(match.group(1), f"build-{match.group(1)}")


# ---------------------------------------------------------------------------
# 1 段の判定（合格の定義はここだけに置く）
# ---------------------------------------------------------------------------


def judge_step(report: dict[str, Any], flow_control: str = "off") -> dict[str, Any]:
    """1 段（1 回の実行）の合否を返す。

    flow_control: "off" は公称値の測定条件（流量制御を外す）、"on" は M-4 の確認条件
    （既定構成で流量制御が効いている）、"any" は条件を問わない。
    """
    series = classify(report)
    recon = report["Reconciliation"]
    counters = recon.get("Counters", {})
    load_options = report.get("LoadGeneratorOptions", {})
    load_result = report.get("LoadResult", {})
    conditions = report.get("Conditions") if isinstance(report.get("Conditions"), dict) else None

    sent = int(recon.get("SentCount", 0))
    accounted_loss = int(recon.get("AccountedLossCount", 0))
    derived_os_loss = int(recon.get("DerivedOsBufferLossCount", 0))
    spool_evacuated = int(recon.get("SpoolEvacuatedCount", counters.get("SpoolEvacuated", 0)))
    flow_control_dropped = int(counters.get("FlowControlDropped", 0))
    reconciled = bool(recon.get("IsReconciled", False))

    result: dict[str, Any] = {
        "series": series,
        "scenario": report.get("ScenarioName"),
        "transport": transport_of(report),
        "provider": provider_of(report),
        "sent": sent,
        "saved": int(recon.get("SavedCount", 0)),
        "accounted_loss": accounted_loss,
        "derived_os_loss": derived_os_loss,
        "spool_evacuated": spool_evacuated,
        "flow_control_dropped": flow_control_dropped,
        "reconciled": reconciled,
        "padding_bytes": int(load_options.get("PaddingBytes", 0)),
        "flow_control_enabled": conditions.get("FlowControlEnabled") if conditions else None,
        "flow_control_overridden": conditions.get("FlowControlOverridden") if conditions else None,
        "udp_receive_buffer_bytes": conditions.get("UdpReceiveBufferBytes") if conditions else None,
        "udp_receive_buffer_overridden": conditions.get("UdpReceiveBufferOverridden") if conditions else None,
        "drain_completed": conditions.get("DrainCompleted") if conditions else None,
        "reasons": [],
        "warnings": [],
    }
    reasons: list[str] = result["reasons"]
    warnings: list[str] = result["warnings"]

    is_burst = report.get("ScenarioName") == "BurstQ1Drop"
    if is_burst:
        result["value"] = int(load_options.get("BurstCount", 0))
        result["duration_seconds"] = None
    else:
        rate = int(load_options.get("RatePerSecond", 0))
        duration = int(load_options.get("DurationSeconds", 0))
        elapsed = parse_timespan(load_result.get("Elapsed", 0))
        # 目標時間より短くは割らない。送出が目標時間を超えて伸びた段（読み取り停止で書き込みが
        # 詰まった TCP など）は、伸びた分だけ実効レートが下がる。
        divisor = max(elapsed, float(duration))
        effective_rate = sent / divisor if divisor > 0 else 0.0
        send_ratio = effective_rate / rate if rate > 0 else 0.0
        result.update({
            "value": rate,
            "duration_seconds": duration,
            "send_elapsed_seconds": round(elapsed, 3),
            "effective_rate": round(effective_rate, 1),
            "send_ratio": round(send_ratio, 4),
        })
        if send_ratio < REQUIRED_SEND_RATIO:
            reasons.append(f"実効送出レートが目標の {send_ratio:.2%}（{REQUIRED_SEND_RATIO:.0%} 未満）")
        if spool_evacuated != 0:
            reasons.append(f"スプール退避 {spool_evacuated} 件")

    if accounted_loss != 0:
        reasons.append(f"計上済みの破棄 {accounted_loss} 件")
    if derived_os_loss != 0:
        reasons.append(f"OS バッファ破棄（導出値）{derived_os_loss} 件")
    if not reconciled:
        reasons.append(f"突合不成立（差分 {recon.get('Difference')}）")

    if conditions is None:
        warnings.append("測定条件（Conditions）の記録がない旧形式の JSON。流量制御の状態を確認できない")
    else:
        enabled = conditions.get("FlowControlEnabled")
        if flow_control == "off" and enabled:
            reasons.append("流量制御が有効のまま測っている（公称値の測定条件外）")
        if flow_control == "on" and not enabled:
            reasons.append("流量制御が無効（M-4 の確認条件外）")
        if conditions.get("DrainCompleted") is False:
            warnings.append("drain 待ちが上限に達し、drain 途中の件数で突合している")
        # Kill で止めた段は、直近の定期永続化以降のカウンタ増分（退避・破棄）が欠け、
        # 実際には退避が起きていても 0 に見え得る。合格の根拠にできないため不合格にする。
        if conditions.get("GracefulStopSucceeded") is False:
            reasons.append("ホストをグレースフル停止できず Kill した（カウンタの最終値が欠け得る）")

    if series is None:
        warnings.append("どの系列にも当たらない実行（シナリオ・トランスポート・保存先の組み合わせ）")

    result["pass"] = not reasons
    return result


# ---------------------------------------------------------------------------
# 標本（ランナー個体）の収集
# ---------------------------------------------------------------------------


def _identity_from_report(report: dict[str, Any], label: str) -> dict[str, Any]:
    env = report.get("Environment", {})
    return {
        "SampleLabel": label,
        "RunnerLabel": version_from_build(env.get("OsDescription")),
        "OsDescription": env.get("OsDescription"),
        "CpuName": env.get("ProcessorName"),
        "ProcessorIdentifier": env.get("ProcessorName"),
        "LogicalProcessors": env.get("LogicalProcessorCount"),
        "TotalPhysicalMemoryBytes": env.get("TotalPhysicalMemoryBytes"),
        "MachineName": env.get("MachineName"),
        "Phase": None,
        "IdentityRecorded": False,
    }


def collect_samples(root: Path) -> list[dict[str, Any]]:
    """root 以下を走査し、標本ごとに識別情報と段の一覧をまとめる。

    runner-identity.json を持つディレクトリが標本の単位。持たない入力（ローカル実行や既存の
    results/）は、結果 JSON を直接含むディレクトリ 1 つを標本 1 つとみなす。
    """
    root = root.resolve()
    identity_dirs = sorted(p.parent for p in root.rglob(IDENTITY_FILE_NAME))
    samples: list[dict[str, Any]] = []
    claimed: set[Path] = set()

    for sample_dir in identity_dirs:
        identity = load_json(sample_dir / IDENTITY_FILE_NAME)
        identity.setdefault("SampleLabel", str(sample_dir.relative_to(root)) or sample_dir.name)
        identity["IdentityRecorded"] = True
        files = sorted(p for p in sample_dir.rglob("*.json") if p.name not in (IDENTITY_FILE_NAME, LADDER_LOG_FILE_NAME))
        reports = []
        for path in files:
            claimed.add(path)
            document = load_json(path)
            if is_scenario_report(document):
                reports.append((path, document))
        missing_steps = _missing_steps_from_ladder_log(sample_dir)
        samples.append({"dir": sample_dir, "identity": identity, "reports": reports, "missing_steps": missing_steps})

    loose: dict[Path, list[tuple[Path, dict[str, Any]]]] = {}
    for path in sorted(root.rglob("*.json")):
        if path in claimed or path.name in (IDENTITY_FILE_NAME, LADDER_LOG_FILE_NAME):
            continue
        if any(parent in identity_dirs for parent in path.parents):
            continue
        document = load_json(path)
        if is_scenario_report(document):
            loose.setdefault(path.parent, []).append((path, document))

    for sample_dir, reports in sorted(loose.items()):
        label = str(sample_dir.relative_to(root)) if sample_dir != root else sample_dir.name
        identity = _identity_from_report(reports[0][1], label)
        samples.append({"dir": sample_dir, "identity": identity, "reports": reports, "missing_steps": []})

    return samples


def _missing_steps_from_ladder_log(sample_dir: Path) -> list[dict[str, Any]]:
    """梯子の記録のうち、結果 JSON が残らなかった段（ベンチの異常終了）を不合格の段として返す。

    結果 JSON だけを数えると、落ちた段が「無かった段」になり R_i を過大に見積もるため。
    """
    log_path = sample_dir / LADDER_LOG_FILE_NAME
    if not log_path.exists():
        return []
    entries = load_json(log_path)
    if isinstance(entries, dict):
        entries = [entries]
    missing = []
    for entry in entries if isinstance(entries, list) else []:
        if not entry.get("Json"):
            missing.append({
                "series": entry.get("Series"),
                "value": int(entry.get("Value", 0)),
                "pass": False,
                "reasons": [f"結果 JSON なし（ベンチの終了コード {entry.get('ExitCode')}）"],
                "warnings": [],
                "file": None,
            })
    return missing


# ---------------------------------------------------------------------------
# 個体ごとの値と分布
# ---------------------------------------------------------------------------


def individual_value(steps: Iterable[dict[str, Any]]) -> dict[str, Any] | None:
    """梯子の段から R_i を求める。

    R_i = そのレートとそれ以下の全レートが合格した最大のレート。最初に落ちた段より上で
    合格した段があっても採らない（合否が単調でない個体は、最初に落ちた段の 1 段下）。
    同じレートを複数回実行した場合は、全回合格でなければ不合格として扱う。
    最下段から不合格なら 0（梯子の下限を下げて測り直す合図）。
    """
    by_value: dict[int, bool] = {}
    for step in steps:
        value = int(step["value"])
        by_value[value] = by_value.get(value, True) and bool(step["pass"])
    if not by_value:
        return None

    values = sorted(by_value)
    r_i = 0
    first_failure = None
    for value in values:
        if by_value[value]:
            r_i = value
        else:
            first_failure = value
            break

    passed_above_failure = first_failure is not None and any(by_value[v] for v in values if v > first_failure)
    return {
        "value": r_i,
        "first_failure": first_failure,
        "censored": first_failure is None,
        "below_ladder": first_failure == values[0],
        "non_monotonic": passed_above_failure,
        "steps_run": len(values),
        "ladder": [[v, by_value[v]] for v in values],
    }


def nearest_rank(sorted_values: list[int], percent: float) -> int:
    rank = max(1, math.ceil(percent / 100.0 * len(sorted_values)))
    return sorted_values[rank - 1]


def nominal_from_lower_bound(lower_bound: int) -> int:
    return math.floor(Fraction(lower_bound) * NOMINAL_FACTOR / NOMINAL_STEP) * NOMINAL_STEP


def distribution(values: list[int]) -> dict[str, Any]:
    ordered = sorted(values)
    n = len(ordered)
    if n == 0:
        return {"n": 0}
    return {
        "n": n,
        "min": ordered[0],
        "p5": nearest_rank(ordered, 5),
        "p10": nearest_rank(ordered, 10),
        "median": statistics.median(ordered),
        "max": ordered[-1],
        "lower_bound": ordered[LOWER_BOUND_RANK - 1] if n >= LOWER_BOUND_RANK else None,
        "lower_bound_valid": n >= SAMPLES_PER_VERSION,
    }


# ---------------------------------------------------------------------------
# 集計
# ---------------------------------------------------------------------------


def expected_flow_control(phase: str) -> str:
    return "on" if phase == "flowcontrol" else "off"


def judge_samples(samples: list[dict[str, Any]], phase: str) -> list[dict[str, Any]]:
    flow_control = expected_flow_control(phase)
    judged = []
    for sample in samples:
        identity = sample["identity"]
        steps = []
        for path, report in sample["reports"]:
            step = judge_step(report, flow_control)
            step["file"] = str(path.relative_to(sample["dir"]))
            steps.append(step)
        steps.extend(sample["missing_steps"])
        judged.append({
            "label": identity.get("SampleLabel"),
            "version": identity.get("RunnerLabel") or version_from_build(identity.get("OsDescription")),
            "cpu": identity.get("CpuName") or identity.get("ProcessorIdentifier") or "(不明)",
            "identity": identity,
            "steps": steps,
        })
    return judged


def condition_key(step: dict[str, Any]) -> str:
    """測定条件の違いで分布を混ぜないためのキー（受信バッファ。M-2 の再確認を別集計にする）。"""
    buffer_bytes = step.get("udp_receive_buffer_bytes")
    overridden = step.get("udp_receive_buffer_overridden")
    if buffer_bytes is None:
        return "rcvbuf=記録なし"
    return f"rcvbuf={buffer_bytes}" + ("" if overridden else "（製品既定）")


def aggregate_ladders(judged: list[dict[str, Any]]) -> dict[str, Any]:
    """explore / sample: 個体ごとの R_i と、版ごとの分布・L・X。"""
    per_sample = []
    for sample in judged:
        values: dict[str, Any] = {}
        for series in SERIES:
            steps = [s for s in sample["steps"] if s.get("series") == series]
            if steps:
                info = individual_value(steps)
                recorded = next((s for s in steps if s.get("file")), None)
                info["condition"] = condition_key(recorded) if recorded else "rcvbuf=記録なし"
                values[series] = info
        per_sample.append({"label": sample["label"], "version": sample["version"], "cpu": sample["cpu"], "values": values})

    series_summary: dict[str, Any] = {}
    for series, spec in SERIES.items():
        groups: dict[str, dict[str, list[tuple[int, dict[str, Any]]]]] = {}
        for sample in per_sample:
            info = sample["values"].get(series)
            if info is None:
                continue
            groups.setdefault(info["condition"], {}).setdefault(sample["version"], []).append((info["value"], sample))
        if not groups:
            continue

        by_condition = {}
        for condition, by_version in sorted(groups.items()):
            versions = {}
            for version, entries in sorted(by_version.items()):
                values = [v for v, _ in entries]
                dist = distribution(values)
                cpu_groups: dict[str, list[int]] = {}
                for value, sample in entries:
                    cpu_groups.setdefault(sample["cpu"], []).append(value)
                dist["by_cpu"] = {
                    cpu: {"n": len(vs), "min": min(vs), "median": statistics.median(vs)}
                    for cpu, vs in sorted(cpu_groups.items())
                }
                dist["censored"] = sum(1 for _, s in entries if s["values"][series]["censored"])
                dist["below_ladder"] = sum(1 for _, s in entries if s["values"][series]["below_ladder"])
                dist["non_monotonic"] = sum(1 for _, s in entries if s["values"][series]["non_monotonic"])
                versions[version] = dist
            by_condition[condition] = {"versions": versions, "nominal": _nominal_candidate(versions) if spec["nominal"] else None}
        series_summary[series] = {"label": spec["label"], "unit": spec["unit"], "by_condition": by_condition}

    return {"samples": per_sample, "series": series_summary}


def _nominal_candidate(versions: dict[str, Any]) -> dict[str, Any]:
    present = [v for v in REQUIRED_VERSIONS if v in versions]
    notes = []
    missing = [v for v in REQUIRED_VERSIONS if v not in versions]
    if missing:
        notes.append(f"{', '.join(missing)} の標本がない（両方の版で測ってから確定する）")
    if not present:
        return {"value": None, "lower_bound": None, "limiting_version": None, "final": False, "notes": notes + ["推奨区分の版の標本がない"]}

    limiting = min(present, key=lambda v: versions[v]["lower_bound"])
    lower_bound = versions[limiting]["lower_bound"]
    for version in present:
        dist = versions[version]
        if not dist["lower_bound_valid"]:
            notes.append(f"{version} は n = {dist['n']}（{SAMPLES_PER_VERSION} 未満。Wilks の保証が成り立たない）")
        if dist["censored"]:
            notes.append(f"{version} の {dist['censored']} 個体は梯子の最上段まで合格（上限未到達。L は控えめに出る）")
        if dist["below_ladder"]:
            notes.append(f"{version} の {dist['below_ladder']} 個体は最下段で不合格（梯子の下限を下げて測り直す）")
    final = not missing and all(versions[v]["lower_bound_valid"] for v in present)
    return {
        "value": nominal_from_lower_bound(lower_bound),
        "lower_bound": lower_bound,
        "limiting_version": limiting,
        "final": final,
        "notes": notes,
    }


def margin_verdict(lower_bound: int, nominal: int) -> dict[str, Any]:
    margin = lower_bound / nominal - 1
    if margin < MARGIN_BREACH:
        verdict = "公称値割れ（性能の修正か、supersession 手続きによる引き下げが要る）"
    elif margin < MARGIN_RELEASE_GATE:
        verdict = "余裕率が閾値未満（リリース判定と同じ重さで扱う）"
    else:
        verdict = "余裕あり"
    return {"lower_bound": lower_bound, "nominal": nominal, "margin": round(margin, 4), "verdict": verdict}


def aggregate_verify(judged: list[dict[str, Any]]) -> dict[str, Any]:
    """verify: 各系列について、指定レート・指定時間の 1 段が全ジョブで合格したか。"""
    result = {}
    for series in SUSTAINED_SERIES:
        jobs = []
        for sample in judged:
            steps = [s for s in sample["steps"] if s.get("series") == series]
            if not steps:
                continue
            jobs.append({
                "label": sample["label"],
                "version": sample["version"],
                "cpu": sample["cpu"],
                "pass": all(s["pass"] for s in steps),
                "rates": sorted({s["value"] for s in steps}),
                "durations": sorted({s.get("duration_seconds") or 0 for s in steps}),
                "reasons": sorted({r for s in steps for r in s["reasons"]}),
            })
        if not jobs:
            continue
        rates = sorted({r for j in jobs for r in j["rates"]})
        durations = sorted({d for j in jobs for d in j["durations"]})
        notes = []
        if len(rates) != 1:
            notes.append(f"レートが 1 つに揃っていない: {rates}")
        if durations and min(durations) < VERIFY_MIN_DURATION_SECONDS:
            notes.append(f"{VERIFY_MIN_DURATION_SECONDS} 秒未満の段がある（最短 {min(durations)} 秒）")
        passed = sum(1 for j in jobs if j["pass"])
        all_pass = passed == len(jobs)
        enough = len(jobs) >= VERIFY_JOBS_REQUIRED
        if not enough:
            notes.append(f"ジョブ数 {len(jobs)}（{VERIFY_JOBS_REQUIRED} 未満）。追加の dispatch で合算する")
        result[series] = {
            "label": SERIES[series]["label"],
            "rates": rates,
            "jobs": len(jobs),
            "passed": passed,
            "long_runs": sum(1 for j in jobs if max(j["durations"]) >= LONG_RUN_DURATION_SECONDS),
            "all_pass": all_pass,
            "verified": all_pass and enough and len(rates) == 1,
            "failed_jobs": [j for j in jobs if not j["pass"]],
            "notes": notes,
        }
    return result


def aggregate_flowcontrol(judged: list[dict[str, Any]]) -> dict[str, Any]:
    """flowcontrol（M-4）: 既定構成で流量制御が 1 件も発火しなかったか。"""
    runs = []
    for sample in judged:
        for step in sample["steps"]:
            if step.get("file") is None:
                runs.append({"label": sample["label"], "series": step.get("series"), "value": step["value"],
                             "fired": None, "flow_control_enabled": None, "default_configuration": None,
                             "nominal_pass": False, "reasons": step["reasons"]})
                continue
            runs.append({
                "label": sample["label"],
                "version": sample["version"],
                "series": step["series"],
                "value": step["value"],
                "fired": step["flow_control_dropped"],
                "flow_control_enabled": step["flow_control_enabled"],
                "default_configuration": step["flow_control_enabled"] is True and step["flow_control_overridden"] is False,
                "nominal_pass": step["pass"],
                "reasons": step["reasons"],
            })
    by_series: dict[str, Any] = {}
    for run in runs:
        entry = by_series.setdefault(run["series"] or "(不明)", {"runs": 0, "fired_runs": 0, "max_dropped": 0, "values": set()})
        entry["runs"] += 1
        entry["values"].add(run["value"])
        if run["fired"]:
            entry["fired_runs"] += 1
            entry["max_dropped"] = max(entry["max_dropped"], run["fired"])
    for entry in by_series.values():
        entry["values"] = sorted(entry["values"])

    problems = []
    if not runs:
        problems.append("実行が 1 件もない")
    if any(r["fired"] is None for r in runs):
        problems.append("結果 JSON の無い実行がある")
    if any(r["flow_control_enabled"] is None for r in runs if r["fired"] is not None):
        problems.append("測定条件の記録が無い実行がある")
    if any(r["default_configuration"] is False for r in runs):
        problems.append("既定構成（流量制御は製品既定で有効）ではない実行がある")
    fired = sum(1 for r in runs if r["fired"])
    return {
        "runs": runs,
        "by_series": by_series,
        "fired_runs": fired,
        "no_firing": fired == 0 and not problems,
        "problems": problems,
    }


def aggregate(root: Path, phase: str, nominal: dict[str, int] | None = None) -> dict[str, Any]:
    samples = collect_samples(root)
    judged = judge_samples(samples, phase)
    warnings = []
    for sample in samples:
        recorded_phase = sample["identity"].get("Phase")
        if recorded_phase and recorded_phase != phase:
            warnings.append(f"{sample['identity'].get('SampleLabel')}: 記録上のフェーズ {recorded_phase} が集計指定 {phase} と異なる")
        if not sample["identity"].get("IdentityRecorded"):
            warnings.append(f"{sample['identity'].get('SampleLabel')}: runner-identity.json がない（結果 JSON の環境欄で代用）")
    paddings = sorted({s.get("padding_bytes") for j in judged for s in j["steps"] if s.get("padding_bytes") is not None})
    old_format = sum(1 for j in judged for s in j["steps"] if s.get("file") and s.get("flow_control_enabled") is None)
    if old_format:
        warnings.append(f"測定条件の記録が無い旧形式の JSON が {old_format} 件ある（流量制御の状態を確認できない）")
    unclassified = sorted({f"{s.get('scenario')}/{s.get('transport')}/{s.get('provider')}"
                           for j in judged for s in j["steps"] if s.get("file") and s.get("series") is None})
    if unclassified:
        warnings.append(f"どの系列にも当たらないため集計から外した実行がある: {', '.join(unclassified)}")

    output: dict[str, Any] = {
        "phase": phase,
        "method": method_description(),
        "sample_count": len(judged),
        "padding_bytes": paddings,
        "warnings": warnings,
        "steps": [
            {"label": j["label"], "version": j["version"], "cpu": j["cpu"], **{k: v for k, v in s.items()}}
            for j in judged for s in j["steps"]
        ],
    }
    if phase in ("explore", "sample"):
        output["ladders"] = aggregate_ladders(judged)
        if nominal:
            output["margins"] = _margins(output["ladders"], nominal)
    elif phase == "verify":
        output["verify"] = aggregate_verify(judged)
        if nominal:
            for series, entry in output["verify"].items():
                if series in nominal and entry["rates"] != [nominal[series]]:
                    entry["notes"].append(f"検証レート {entry['rates']} が公称値 {nominal[series]} と一致しない")
                    entry["verified"] = False
    elif phase == "flowcontrol":
        output["flowcontrol"] = aggregate_flowcontrol(judged)
    return output


def _margins(ladders: dict[str, Any], nominal: dict[str, int]) -> dict[str, Any]:
    margins = {}
    for series, value in nominal.items():
        summary = ladders["series"].get(series)
        if not summary:
            continue
        for condition, data in summary["by_condition"].items():
            versions = data["versions"]
            present = [v for v in REQUIRED_VERSIONS if v in versions]
            if not present:
                continue
            lower_bound = min(versions[v]["lower_bound"] for v in present)
            margins.setdefault(series, {})[condition] = margin_verdict(lower_bound, value)
    return margins


def method_description() -> dict[str, Any]:
    return {
        "samples_per_version": SAMPLES_PER_VERSION,
        "lower_bound_rank": LOWER_BOUND_RANK,
        "nominal_factor": str(NOMINAL_FACTOR),
        "nominal_step": NOMINAL_STEP,
        "required_versions": list(REQUIRED_VERSIONS),
        "required_send_ratio": REQUIRED_SEND_RATIO,
        "pass_definition": [
            "計上済みの破棄が 0",
            "OS バッファ破棄（導出値）が 0",
            "スプール退避が 0（持続系列のみ。バーストは退避を許容し損失 0 を見る）",
            f"実効送出レートが目標の {REQUIRED_SEND_RATIO:.0%} 以上（持続系列のみ）",
            "突合成立",
            "公称値の測定では流量制御を外している",
            "ホストをグレースフル停止できている（Kill の段はカウンタが欠け得るため合格にしない）",
        ],
        "verify_jobs_required": VERIFY_JOBS_REQUIRED,
        "verify_min_duration_seconds": VERIFY_MIN_DURATION_SECONDS,
        "margin_release_gate": MARGIN_RELEASE_GATE,
        "margin_breach": MARGIN_BREACH,
    }


# ---------------------------------------------------------------------------
# Markdown
# ---------------------------------------------------------------------------


def _fmt(value: Any) -> str:
    if value is None:
        return "—"
    if isinstance(value, float) and value.is_integer():
        return f"{int(value):,}"
    if isinstance(value, (int, float)):
        return f"{value:,}"
    return str(value)


def render_markdown(output: dict[str, Any]) -> str:
    lines = [f"# 性能公称値・流量制御の調査（phase: {output['phase']}）", ""]
    m = output["method"]
    lines.append(
        f"算出方法: 版あたり n = {m['samples_per_version']}、L = 小さい方から {m['lower_bound_rank']} 番目、"
        f"X = floor(L × {m['nominal_factor']} / {m['nominal_step']}) × {m['nominal_step']}、"
        f"版 {' / '.join(m['required_versions'])} の小さい方を採る。"
    )
    lines.append("1 段の合格: " + "、".join(m["pass_definition"]) + "。")
    lines.append("")
    paddings = ", ".join(str(p) for p in output["padding_bytes"]) or "—"
    lines.append(f"標本（ジョブ）数: {output['sample_count']}　メッセージのパディング: {paddings} バイト")
    lines.append("")
    if output["warnings"]:
        lines.append("> [!WARNING]")
        for warning in output["warnings"]:
            lines.append(f"> - {warning}")
        lines.append("")

    if "ladders" in output:
        lines.extend(_render_ladders(output))
    if "verify" in output:
        lines.extend(_render_verify(output["verify"]))
    if "flowcontrol" in output:
        lines.extend(_render_flowcontrol(output["flowcontrol"]))
    return "\n".join(lines).rstrip() + "\n"


def _render_ladders(output: dict[str, Any]) -> list[str]:
    ladders = output["ladders"]
    lines: list[str] = []
    if output["phase"] == "explore":
        lines += ["> 探索フェーズの集計。梯子の範囲を決めるための参考で、公称値の算出には使わない。", ""]
    for series, summary in ladders["series"].items():
        for condition, data in summary["by_condition"].items():
            lines.append(f"## {series}: {summary['label']}（{condition}）")
            lines.append("")
            lines.append("| 版 | n | 最小 | P5 | P10 | 中央値 | 最大 | 上限未到達 | 最下段不合格 | 非単調 |")
            lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
            for version, d in data["versions"].items():
                lines.append(
                    f"| {version} | {d['n']} | {_fmt(d['min'])} | {_fmt(d['p5'])} | {_fmt(d['p10'])} | "
                    f"{_fmt(d['median'])} | {_fmt(d['max'])} | {d['censored']} | {d['below_ladder']} | {d['non_monotonic']} |"
                )
            lines.append("")
            nominal = data.get("nominal")
            if nominal is not None:
                if nominal["value"] is None:
                    lines.append("公称値 X: 算出できない")
                else:
                    state = "確定条件を満たす" if nominal["final"] else "暫定（確定条件を満たさない）"
                    lines.append(
                        f"**公称値 X = {_fmt(nominal['value'])} {summary['unit']}**（L = {_fmt(nominal['lower_bound'])}、"
                        f"律速の版 = {nominal['limiting_version']}。{state}）"
                    )
                for note in nominal["notes"]:
                    lines.append(f"- {note}")
                lines.append("")
            lines.append("<details><summary>CPU 別の内訳</summary>")
            lines.append("")
            lines.append("| 版 | CPU | n | 最小 | 中央値 |")
            lines.append("|---|---|---:|---:|---:|")
            for version, d in data["versions"].items():
                for cpu, c in d["by_cpu"].items():
                    lines.append(f"| {version} | {cpu} | {c['n']} | {_fmt(c['min'])} | {_fmt(c['median'])} |")
            lines.append("")
            lines.append("</details>")
            lines.append("")

    if "margins" in output:
        lines.append("## 余裕率 h = L / X − 1")
        lines.append("")
        lines.append("| 系列 | 条件 | L | X | h | 区分 |")
        lines.append("|---|---|---:|---:|---:|---|")
        for series, by_condition in output["margins"].items():
            for condition, mv in by_condition.items():
                lines.append(f"| {series} | {condition} | {_fmt(mv['lower_bound'])} | {_fmt(mv['nominal'])} | {mv['margin']:.1%} | {mv['verdict']} |")
        lines.append("")

    present_series = [s for s in SERIES if any(s in sample["values"] for sample in ladders["samples"])]
    if present_series:
        lines.append("## 個体ごとの値 R_i")
        lines.append("")
        lines.append("| 標本 | 版 | CPU | " + " | ".join(present_series) + " |")
        lines.append("|---|---|---|" + "---:|" * len(present_series))
        for sample in ladders["samples"]:
            cells = []
            for series in present_series:
                info = sample["values"].get(series)
                if info is None:
                    cells.append("—")
                    continue
                mark = "↑" if info["censored"] else ("*" if info["non_monotonic"] else "")
                cells.append(f"{_fmt(info['value'])}{mark}")
            lines.append(f"| {sample['label']} | {sample['version']} | {sample['cpu']} | " + " | ".join(cells) + " |")
        lines.append("")
        lines.append("↑ = 梯子の最上段まで合格（上限未到達）、* = 合否が単調でない（最初に落ちた段の 1 段下を採用）")
        lines.append("")
    return lines


def _render_verify(verify: dict[str, Any]) -> list[str]:
    lines = ["## 確定時の検証（全数合格）", "", "| 系列 | レート | ジョブ | 合格 | 長時間版 | 判定 |", "|---|---|---:|---:|---:|---|"]
    for series, entry in verify.items():
        verdict = "合格" if entry["verified"] else ("全数合格（条件未達）" if entry["all_pass"] else "不合格")
        lines.append(
            f"| {series} | {', '.join(_fmt(r) for r in entry['rates'])} | {entry['jobs']} | {entry['passed']} | "
            f"{entry['long_runs']} | {verdict} |"
        )
    lines.append("")
    for series, entry in verify.items():
        for note in entry["notes"]:
            lines.append(f"- {series}: {note}")
        for job in entry["failed_jobs"]:
            lines.append(f"- {series}: {job['label']}（{job['version']} / {job['cpu']}）不合格: {'、'.join(job['reasons'])}")
    lines.append("")
    return lines


def _render_flowcontrol(fc: dict[str, Any]) -> list[str]:
    verdict = "発火 0（全実行で流量制御による破棄なし）" if fc["no_firing"] else "発火あり、または判定できない"
    lines = [f"## M-4: 既定構成での流量制御の発火 — {verdict}", "",
             "| 系列 | 値 | 実行数 | 発火した実行 | 最大の破棄件数 |", "|---|---|---:|---:|---:|"]
    for series, entry in fc["by_series"].items():
        lines.append(
            f"| {series} | {', '.join(_fmt(v) for v in entry['values'])} | {entry['runs']} | {entry['fired_runs']} | {_fmt(entry['max_dropped'])} |"
        )
    lines.append("")
    for problem in fc["problems"]:
        lines.append(f"- {problem}")
    if fc["problems"]:
        lines.append("")
    return lines


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def parse_nominal(text: str | None) -> dict[str, int] | None:
    if not text:
        return None
    result = {}
    for part in text.split(","):
        part = part.strip()
        if not part:
            continue
        key, _, value = part.partition("=")
        key = key.strip().upper()
        if key not in SERIES or not value.strip().isdigit() or int(value) <= 0:
            raise argparse.ArgumentTypeError(f"--nominal の要素が不正: {part!r}（例: N1=4500,N2=6000）")
        result[key] = int(value)
    return result


def verdict_is_negative(output: dict[str, Any]) -> bool:
    if "verify" in output:
        return not output["verify"] or not all(e["verified"] for e in output["verify"].values())
    if "flowcontrol" in output:
        return not output["flowcontrol"]["no_firing"]
    return False


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    judge = sub.add_parser("judge", help="結果 JSON 1 件の合否を 1 行の JSON で出す（梯子の打ち切り判定用）")
    judge.add_argument("report", type=Path)
    judge.add_argument("--flow-control", choices=("off", "on", "any"), default="off")

    agg = sub.add_parser("aggregate", help="artifact 群を集計する")
    agg.add_argument("root", type=Path)
    agg.add_argument("--phase", choices=PHASES, required=True)
    agg.add_argument("--out-json", type=Path)
    agg.add_argument("--out-md", type=Path)
    agg.add_argument("--nominal", type=parse_nominal, help="既定の公称値（余裕率・検証レートの照合に使う）。例: N1=4500,N2=6000")
    agg.add_argument("--strict", action="store_true", help="verify / flowcontrol の判定が否定的なら終了コード 1")

    args = parser.parse_args(argv)

    if args.command == "judge":
        report = load_json(args.report)
        if not is_scenario_report(report):
            print(json.dumps({"pass": False, "reasons": ["ScenarioReport 形式ではない"]}, ensure_ascii=False))
            return 2
        print(json.dumps(judge_step(report, args.flow_control), ensure_ascii=False))
        return 0

    output = aggregate(args.root, args.phase, args.nominal)
    markdown = render_markdown(output)
    if args.out_json:
        args.out_json.write_text(json.dumps(output, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    if args.out_md:
        args.out_md.write_text(markdown, encoding="utf-8")
    if not args.out_json and not args.out_md:
        sys.stdout.write(markdown)
    return 1 if args.strict and verdict_is_negative(output) else 0


if __name__ == "__main__":
    sys.exit(main())
