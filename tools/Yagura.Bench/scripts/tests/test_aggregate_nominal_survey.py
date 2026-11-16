"""aggregate-nominal-survey.py の単体テスト（標準ライブラリの unittest のみ）。

実行: python3 -m unittest discover -s tools/Yagura.Bench/scripts/tests -v

結果 JSON の雛形には tools/Yagura.Bench/results/ に収録済みの実測 JSON を使い、合否に関わる
欄だけを書き換える。ベンチの JSON 形式が変わったときに、集計側が追従していないことを
ここで検出するため。
"""

from __future__ import annotations

import contextlib
import copy
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "aggregate-nominal-survey.py"
BENCH_ROOT = Path(__file__).resolve().parents[2]
M5_RESULTS = BENCH_ROOT / "results" / "2026-07-06-ci-windows-latest-m5"
TEMPLATE = M5_RESULTS / "attempt-4" / "SustainedZeroDrop-20260705-153954.json"

_spec = importlib.util.spec_from_file_location("aggregate_nominal_survey", SCRIPT)
survey = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(survey)

BUILD = {"windows-2022": "10.0.20348", "windows-2025": "10.0.26100"}


def make_report(
    scenario: str = "SustainedZeroDrop",
    transport: int | str = 0,
    rate: int = 5000,
    duration: int = 60,
    sent: int | None = None,
    elapsed: str | None = None,
    accounted_loss: int = 0,
    derived_os_loss: int = 0,
    spool_evacuated: int = 0,
    flow_control_dropped: int = 0,
    reconciled: bool = True,
    burst_count: int = 0,
    provider: str = "Sqlite",
    flow_control_enabled: bool | None = False,
    flow_control_overridden: bool = True,
    conditions: bool = True,
    graceful_stop: bool | None = True,
    os_build: str = "10.0.26100",
    cpu: str = "AMD EPYC 7763 64-Core Processor",
) -> dict:
    """収録済みの実測 JSON を雛形に、合否に関わる欄だけを差し替えた結果 JSON を作る。"""
    report = copy.deepcopy(json.loads(TEMPLATE.read_text(encoding="utf-8-sig")))
    report["ScenarioName"] = scenario
    report["Environment"]["OsDescription"] = f"Microsoft Windows {os_build}"
    report["Environment"]["ProcessorName"] = cpu
    options = report["LoadGeneratorOptions"]
    options["Transport"] = transport
    options["Pattern"] = 1 if scenario == "BurstQ1Drop" else 0
    options["RatePerSecond"] = 0 if scenario == "BurstQ1Drop" else rate
    options["DurationSeconds"] = 0 if scenario == "BurstQ1Drop" else duration
    options["BurstCount"] = burst_count
    options["PaddingBytes"] = 100
    if sent is None:
        sent = burst_count if scenario == "BurstQ1Drop" else rate * duration
    report["LoadResult"].update({"AttemptedCount": sent, "SucceededCount": sent, "SentCount": sent,
                                 "Elapsed": elapsed or f"00:{duration // 60:02d}:{duration % 60:02d}.0100000"})
    saved = sent - accounted_loss - derived_os_loss
    report["Reconciliation"].update({
        "SentCount": sent,
        "SavedCount": saved,
        "AccountedLossCount": accounted_loss,
        "SpoolEvacuatedCount": spool_evacuated,
        "DerivedOsBufferLossCount": derived_os_loss,
        "Difference": derived_os_loss if reconciled else -1,
        "IsReconciled": reconciled,
    })
    report["Reconciliation"]["Counters"].update({
        "InternalBufferDropped": accounted_loss - flow_control_dropped,
        "SpoolEvacuated": spool_evacuated,
        "FlowControlDropped": flow_control_dropped,
    })
    report["AdditionalMetrics"] = {"provider": provider} if scenario == "ProviderWriteCeiling" else {}
    if conditions:
        report["Conditions"] = {
            "Provider": provider,
            "FlowControlEnabled": flow_control_enabled,
            "FlowControlMessagesPerSecond": 10000 if flow_control_enabled else None,
            "FlowControlBurstSize": 20000 if flow_control_enabled else None,
            "FlowControlOverridden": flow_control_overridden,
            "UdpReceiveBufferBytes": 1048576,
            "UdpReceiveBufferOverridden": False,
            "DrainTimeoutSeconds": 120,
            "DrainCompleted": True,
            "DrainWaitElapsed": "00:00:00.0010000",
            "GracefulStopSucceeded": graceful_stop,
        }
    else:
        report.pop("Conditions", None)
    return report


def write_sample(root: Path, label: str, version: str, steps: list[dict], phase: str = "sample",
                 cpu: str = "AMD EPYC 7763 64-Core Processor", ladder_log: list[dict] | None = None,
                 inputs: dict | None = None) -> Path:
    sample_dir = root / label
    sample_dir.mkdir(parents=True)
    identity = {
        "Phase": phase,
        "RunnerLabel": version,
        "SampleIndex": label,
        "OsDescription": f"Microsoft Windows {BUILD[version]}",
        "CpuName": cpu,
        "LogicalProcessors": 4,
    }
    if inputs is not None:
        identity["Inputs"] = inputs
    (sample_dir / "runner-identity.json").write_text(json.dumps(identity), encoding="utf-8-sig")
    for i, report in enumerate(steps):
        series_dir = sample_dir / report["ScenarioName"]
        series_dir.mkdir(exist_ok=True)
        (series_dir / f"step-{i:02d}.json").write_text(json.dumps(report), encoding="utf-8")
    if ladder_log is not None:
        (sample_dir / "ladder-log.json").write_text(json.dumps(ladder_log), encoding="utf-8")
    return sample_dir


def udp_ladder(capacity: int, rates=(2000, 4000, 6000, 8000), version: str = "windows-2025", cpu: str = "cpu-a") -> list[dict]:
    """capacity 以下のレートは合格、超えるレートは Q1 破棄で不合格になる N1 の梯子。"""
    return [
        make_report(rate=r, accounted_loss=0 if r <= capacity else 100, os_build=BUILD[version], cpu=cpu)
        for r in rates
    ]


class MethodConstantsTests(unittest.TestCase):
    """算出方法の定数を固定する。変える PR はこのテストも変えることになり、差分として見える。"""

    def test_constants_match_the_fixed_method(self):
        self.assertEqual(survey.SAMPLES_PER_VERSION, 29)
        self.assertEqual(survey.LOWER_BOUND_RANK, 1)
        self.assertEqual(survey.NOMINAL_FACTOR, survey.Fraction(4, 5))
        self.assertEqual(survey.NOMINAL_STEP, 500)
        self.assertEqual(survey.REQUIRED_SEND_RATIO, 0.99)
        self.assertEqual(survey.VERIFY_JOBS_REQUIRED, 30)
        self.assertEqual(survey.VERIFY_MIN_DURATION_SECONDS, 300)
        self.assertEqual(survey.REQUIRED_VERSIONS, ("windows-2022", "windows-2025"))
        self.assertEqual(survey.MARGIN_RELEASE_GATE, 0.10)

    def test_sample_sizes_give_the_stated_guarantees(self):
        # n = 29 の最小値: 95% 信頼で 90% 被覆。
        self.assertGreaterEqual(1 - 0.9 ** survey.SAMPLES_PER_VERSION, 0.95)
        # K = 30 全数合格: 個体ごとの不合格率の 95% 上側信頼限界は 1 - 0.05^(1/30) ≈ 9.5%。
        self.assertAlmostEqual(1 - 0.05 ** (1 / survey.VERIFY_JOBS_REQUIRED), 0.095, places=3)


class ParseTimespanTests(unittest.TestCase):
    def test_parses_dotnet_default_format(self):
        self.assertAlmostEqual(survey.parse_timespan("00:00:10.0259653"), 10.0259653)
        self.assertAlmostEqual(survey.parse_timespan("1.02:03:04.5"), 93784.5)
        self.assertEqual(survey.parse_timespan("00:01:00"), 60.0)


class JudgeStepTests(unittest.TestCase):
    def test_recorded_m5_sample_with_q1_drop_fails_with_reasons(self):
        report = json.loads(TEMPLATE.read_text(encoding="utf-8-sig"))

        result = survey.judge_step(report)

        self.assertFalse(result["pass"])
        self.assertEqual(result["series"], "N1")
        self.assertIn("計上済みの破棄 28355 件", result["reasons"])
        self.assertIn("スプール退避 176 件", result["reasons"])
        self.assertTrue(any("旧形式" in w for w in result["warnings"]))

    def test_clean_step_passes(self):
        result = survey.judge_step(make_report())

        self.assertTrue(result["pass"], result["reasons"])
        self.assertEqual(result["value"], 5000)

    def test_spool_evacuation_alone_fails(self):
        # ベンチの「破棄ゼロ維持」フラグは退避を見ない。公称値の合格は退避 0 も要求する。
        result = survey.judge_step(make_report(spool_evacuated=1))

        self.assertFalse(result["pass"])
        self.assertEqual(result["reasons"], ["スプール退避 1 件"])

    def test_send_ratio_threshold(self):
        self.assertTrue(survey.judge_step(make_report(sent=297_000, elapsed="00:01:00"))["pass"])  # 99.0%
        self.assertFalse(survey.judge_step(make_report(sent=296_900, elapsed="00:01:00"))["pass"])

    def test_send_that_overruns_the_duration_lowers_the_effective_rate(self):
        # 目標件数ちょうどを送れても、61 秒かかったなら実効レートは 98.4%。
        result = survey.judge_step(make_report(sent=300_000, elapsed="00:01:01"))

        self.assertFalse(result["pass"])
        self.assertAlmostEqual(result["send_ratio"], 300_000 / 61 / 5000, places=4)

    def test_derived_os_loss_and_reconciliation_failures(self):
        self.assertIn("OS バッファ破棄（導出値）3 件", survey.judge_step(make_report(derived_os_loss=3))["reasons"])
        self.assertFalse(survey.judge_step(make_report(reconciled=False))["pass"])

    def test_flow_control_condition(self):
        enabled = make_report(flow_control_enabled=True, flow_control_overridden=False)

        self.assertFalse(survey.judge_step(enabled, "off")["pass"])
        self.assertTrue(survey.judge_step(enabled, "on")["pass"])
        self.assertTrue(survey.judge_step(enabled, "any")["pass"])
        self.assertFalse(survey.judge_step(make_report(flow_control_enabled=False), "on")["pass"])

    def test_kill_fallback_is_not_trusted_as_a_pass(self):
        # Kill では直近の永続化以降の退避・破棄が欠け、0 に見え得る。
        result = survey.judge_step(make_report(graceful_stop=False))

        self.assertFalse(result["pass"])
        self.assertTrue(any("Kill" in r for r in result["reasons"]))
        self.assertTrue(survey.judge_step(make_report(graceful_stop=None))["pass"])

    def test_old_format_without_conditions_is_judged_on_counters_with_warning(self):
        result = survey.judge_step(make_report(conditions=False))

        self.assertTrue(result["pass"])
        self.assertTrue(result["warnings"])

    def test_burst_allows_spool_but_not_loss(self):
        self.assertTrue(survey.judge_step(make_report(scenario="BurstQ1Drop", burst_count=2000, spool_evacuated=50))["pass"])
        self.assertFalse(survey.judge_step(make_report(scenario="BurstQ1Drop", burst_count=5000, accounted_loss=10))["pass"])

    def test_series_classification(self):
        self.assertEqual(survey.classify(make_report(transport=1)), "N2")
        self.assertEqual(survey.classify(make_report(transport="Tcp")), "N2")
        self.assertEqual(survey.classify(make_report(scenario="ProviderWriteCeiling", transport=1)), "N3")
        self.assertEqual(survey.classify(make_report(scenario="ProviderWriteCeiling", transport=1, provider="SqlServer")), "REF-SQLSERVER")
        self.assertIsNone(survey.classify(make_report(scenario="ProviderWriteCeiling", transport=0)))


class IndividualValueTests(unittest.TestCase):
    @staticmethod
    def steps(*pairs):
        return [{"value": v, "pass": p} for v, p in pairs]

    def test_monotonic_ladder(self):
        info = survey.individual_value(self.steps((1000, True), (2000, True), (3000, False), (4000, False)))

        self.assertEqual(info["value"], 2000)
        self.assertEqual(info["first_failure"], 3000)
        self.assertFalse(info["censored"])

    def test_non_monotonic_takes_one_step_below_first_failure(self):
        info = survey.individual_value(self.steps((1000, True), (2000, False), (3000, True), (4000, False)))

        self.assertEqual(info["value"], 1000)
        self.assertTrue(info["non_monotonic"])

    def test_all_pass_is_censored(self):
        info = survey.individual_value(self.steps((1000, True), (2000, True)))

        self.assertEqual(info["value"], 2000)
        self.assertTrue(info["censored"])

    def test_lowest_failure_is_zero(self):
        info = survey.individual_value(self.steps((1000, False), (2000, False)))

        self.assertEqual(info["value"], 0)
        self.assertTrue(info["below_ladder"])

    def test_repeated_rate_needs_every_run_to_pass(self):
        info = survey.individual_value(self.steps((1000, True), (2000, True), (2000, False), (3000, True)))

        self.assertEqual(info["value"], 1000)


class NominalArithmeticTests(unittest.TestCase):
    def test_nominal_from_lower_bound(self):
        self.assertEqual(survey.nominal_from_lower_bound(6000), 4500)   # 4800 → 4500
        self.assertEqual(survey.nominal_from_lower_bound(6250), 5000)   # 5000 ちょうど
        self.assertEqual(survey.nominal_from_lower_bound(12_345), 9500)  # 9876 → 9500
        self.assertEqual(survey.nominal_from_lower_bound(624), 0)

    def test_distribution_uses_nearest_rank(self):
        dist = survey.distribution([v * 100 for v in range(1, 30)])

        self.assertEqual(dist["n"], 29)
        self.assertEqual(dist["min"], 100)
        self.assertEqual(dist["p5"], 200)
        self.assertEqual(dist["p10"], 300)
        self.assertEqual(dist["median"], 1500)
        self.assertEqual(dist["lower_bound"], 100)
        self.assertTrue(dist["lower_bound_valid"])
        self.assertFalse(survey.distribution([100] * 28)["lower_bound_valid"])

    def test_margin_verdict(self):
        self.assertIn("割れ", survey.margin_verdict(4000, 4500)["verdict"])
        self.assertIn("閾値未満", survey.margin_verdict(4900, 4500)["verdict"])
        self.assertEqual(survey.margin_verdict(6000, 4500)["verdict"], "余裕あり")


class AggregateSampleTests(unittest.TestCase):
    def test_nominal_uses_the_smaller_lower_bound_of_both_versions(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for i in range(29):
                write_sample(root, f"w25-{i:02d}", "windows-2025", udp_ladder(8000 if i else 6000, version="windows-2025"))
                write_sample(root, f"w22-{i:02d}", "windows-2022", udp_ladder(6000 if i else 4000, version="windows-2022", cpu="cpu-b"))

            output = survey.aggregate(root, "sample")

        n1 = output["ladders"]["series"]["N1"]["by_condition"]
        self.assertEqual(len(n1), 1)
        data = next(iter(n1.values()))
        self.assertEqual(data["versions"]["windows-2025"]["min"], 6000)
        self.assertEqual(data["versions"]["windows-2022"]["min"], 4000)
        self.assertEqual(data["versions"]["windows-2025"]["censored"], 28)
        nominal = data["nominal"]
        self.assertEqual(nominal["lower_bound"], 4000)
        self.assertEqual(nominal["limiting_version"], "windows-2022")
        self.assertEqual(nominal["value"], 3000)  # 4000 × 0.8 = 3200 → 3000
        self.assertTrue(nominal["final"])

    def test_single_version_or_small_n_is_provisional(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for i in range(5):
                write_sample(root, f"w25-{i}", "windows-2025", udp_ladder(6000))

            output = survey.aggregate(root, "explore")

        nominal = next(iter(output["ladders"]["series"]["N1"]["by_condition"].values()))["nominal"]
        self.assertFalse(nominal["final"])
        self.assertTrue(any("windows-2022" in note for note in nominal["notes"]))
        self.assertTrue(any("n = 5" in note for note in nominal["notes"]))

    def test_step_without_json_counts_as_failure(self):
        # 2000 の段でベンチが落ちて JSON が無い。3000 が通っていても R_i は 1000。
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_sample(
                root, "w25-0", "windows-2025",
                [make_report(rate=1000), make_report(rate=3000)],
                ladder_log=[
                    {"Series": "N1", "Value": 1000, "Json": "SustainedZeroDrop/step-00.json", "ExitCode": 0},
                    {"Series": "N1", "Value": 2000, "Json": None, "ExitCode": 1},
                    {"Series": "N1", "Value": 3000, "Json": "SustainedZeroDrop/step-01.json", "ExitCode": 0},
                ])

            output = survey.aggregate(root, "sample")

        info = output["ladders"]["samples"][0]["values"]["N1"]
        self.assertEqual(info["value"], 1000)
        self.assertEqual(info["first_failure"], 2000)

    def test_flow_control_left_on_fails_every_nominal_step(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_sample(root, "w25-0", "windows-2025",
                         [make_report(rate=r, flow_control_enabled=True, flow_control_overridden=False) for r in (1000, 2000)])

            output = survey.aggregate(root, "sample")

        self.assertEqual(output["ladders"]["samples"][0]["values"]["N1"]["value"], 0)

    def test_receive_buffer_conditions_are_not_mixed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_sample(root, "a", "windows-2025", udp_ladder(6000))
            report = make_report(rate=2000)
            report["Conditions"]["UdpReceiveBufferBytes"] = 4194304
            report["Conditions"]["UdpReceiveBufferOverridden"] = True
            write_sample(root, "b", "windows-2025", [report])

            output = survey.aggregate(root, "sample")

        self.assertEqual(len(output["ladders"]["series"]["N1"]["by_condition"]), 2)

    def test_margins_against_given_nominal(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_sample(root, "a", "windows-2025", udp_ladder(6000))
            write_sample(root, "b", "windows-2022", udp_ladder(4000, version="windows-2022"))

            output = survey.aggregate(root, "sample", nominal={"N1": 4500})

        margin = next(iter(output["margins"]["N1"].values()))
        self.assertEqual(margin["lower_bound"], 4000)
        self.assertIn("割れ", margin["verdict"])

    @staticmethod
    def write_population(root: Path, w22: int = 29, w25: int = 29) -> None:
        """両版の標本（windows-2025 の上限 8000、windows-2022 の上限 6000）。"""
        for i in range(w25):
            write_sample(root, f"w25-{i:02d}", "windows-2025", udp_ladder(8000, version="windows-2025"))
        for i in range(w22):
            write_sample(root, f"w22-{i:02d}", "windows-2022", udp_ladder(6000, version="windows-2022"))

    @staticmethod
    def n1_missing_log(rates=(2000, 4000)) -> list[dict]:
        return [{"Series": "N1", "Value": r, "Json": None, "ExitCode": 1} for r in rates]

    def test_series_with_only_missing_steps_stays_in_the_sample_condition(self):
        # N1 の 2 段がどちらも結果 JSON なし（ホストの起動失敗など）で梯子が打ち切られた個体。
        # 同じ標本の N2 には記録がある。この最悪の個体を「記録なし」の別表へ逃がさない。
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.write_population(root)
            write_sample(root, "w22-broken", "windows-2022",
                         [make_report(transport=1, rate=2000, os_build=BUILD["windows-2022"])],
                         ladder_log=self.n1_missing_log())

            output = survey.aggregate(root, "sample")

        n1 = output["ladders"]["series"]["N1"]["by_condition"]
        self.assertEqual(list(n1), ["rcvbuf=1048576（製品既定）"])
        data = n1["rcvbuf=1048576（製品既定）"]
        self.assertEqual(data["versions"]["windows-2022"]["n"], 30)
        self.assertEqual(data["versions"]["windows-2022"]["min"], 0)
        self.assertEqual(data["versions"]["windows-2022"]["below_ladder"], 1)
        self.assertEqual(data["nominal"]["value"], 0)
        self.assertFalse(data["nominal"]["final"])

    def test_sample_without_any_result_json_uses_the_recorded_inputs(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.write_population(root, w22=1, w25=1)
            # 入力が空欄（製品既定）→ ほかの標本が記録した製品既定の表へ。
            write_sample(root, "w22-default", "windows-2022", [], ladder_log=self.n1_missing_log(),
                         inputs={"Rcvbuf": ""})
            # PowerShell は空の環境変数を null で書くことがある。
            write_sample(root, "w22-null", "windows-2022", [], ladder_log=self.n1_missing_log(),
                         inputs={"Rcvbuf": None})
            # 入力で指定した受信バッファ → その値の表へ。
            write_sample(root, "w22-4m", "windows-2022", [], ladder_log=self.n1_missing_log(),
                         inputs={"Rcvbuf": "4194304"})

            output = survey.aggregate(root, "sample")

        samples = {s["label"]: s for s in output["ladders"]["samples"]}
        self.assertEqual(samples["w22-default"]["values"]["N1"]["condition"], "rcvbuf=1048576（製品既定）")
        self.assertEqual(samples["w22-null"]["values"]["N1"]["condition"], "rcvbuf=1048576（製品既定）")
        self.assertEqual(samples["w22-4m"]["values"]["N1"]["condition"], "rcvbuf=4194304")
        self.assertNotIn(survey.UNRECORDED_CONDITION, output["ladders"]["series"]["N1"]["by_condition"])

    def test_undeterminable_condition_blocks_final_for_every_table_of_the_series(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.write_population(root)
            # 結果 JSON も測定入力の記録も無い個体は、条件を決められない。
            write_sample(root, "w22-unknown", "windows-2022", [], ladder_log=self.n1_missing_log())

            output = survey.aggregate(root, "sample")

        n1 = output["ladders"]["series"]["N1"]["by_condition"]
        self.assertIn(survey.UNRECORDED_CONDITION, n1)
        main = n1["rcvbuf=1048576（製品既定）"]
        self.assertEqual(main["versions"]["windows-2022"]["n"], 29)
        self.assertFalse(main["nominal"]["final"])
        self.assertTrue(any("条件を決められない" in note for note in main["nominal"]["notes"]))
        self.assertTrue(any(w.startswith("N1:") and "条件を決められない" in w for w in output["warnings"]))

    def test_below_ladder_individual_makes_the_nominal_provisional(self):
        # 最下段 2000 を 1 台が落とすと L = 0。0 は実測値ではないので確定扱いにしない。
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.write_population(root, w22=28)
            write_sample(root, "w22-slow", "windows-2022", udp_ladder(1000, version="windows-2022"))

            output = survey.aggregate(root, "sample")

        data = next(iter(output["ladders"]["series"]["N1"]["by_condition"].values()))
        self.assertEqual(data["nominal"]["lower_bound"], 0)
        self.assertFalse(data["nominal"]["final"])
        self.assertIn("暫定", survey.render_markdown(output))

    def test_recorded_m5_results_aggregate_as_six_samples(self):
        output = survey.aggregate(M5_RESULTS, "explore")

        samples = {s["label"]: s for s in output["ladders"]["samples"]}
        self.assertEqual(len(samples), 6)
        self.assertEqual(sorted(s["values"]["N1"]["value"] for s in samples.values()), [0, 0, 0, 5000, 5000, 5000])
        self.assertTrue(all(s["version"] == "windows-2025" for s in samples.values()))
        self.assertTrue(any("ProviderWriteCeiling/udp/Sqlite" in w for w in output["warnings"]))


class AggregateVerifyTests(unittest.TestCase):
    @staticmethod
    def build(root: Path, jobs_per_version: int, failing: int = 0, rate: int = 4500, duration: int = 300,
              versions: tuple[str, ...] = ("windows-2022", "windows-2025")):
        """各版に jobs_per_version ジョブ。先頭の版の先頭 failing ジョブは Q1 破棄で不合格。"""
        for version in versions:
            for i in range(jobs_per_version):
                loss = 5 if version == versions[0] and i < failing else 0
                write_sample(root, f"{version}-{i:02d}", version,
                             [make_report(rate=rate, duration=duration, accounted_loss=loss, os_build=BUILD[version])],
                             phase="verify")

    def test_thirty_jobs_per_version_all_passing_verifies(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 30)
            output = survey.aggregate(Path(tmp), "verify", nominal={"N1": 4500})

        entry = output["verify"]["N1"]
        self.assertEqual((entry["jobs"], entry["passed"]), (60, 60))
        self.assertEqual(entry["jobs_by_version"], {"windows-2022": 30, "windows-2025": 30})
        self.assertEqual(entry["min_duration_seconds"], 300)
        self.assertTrue(entry["verified"])
        self.assertFalse(survey.verdict_is_negative(output))

    def test_one_failure_rejects(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 30, failing=1)
            output = survey.aggregate(Path(tmp), "verify")

        self.assertFalse(output["verify"]["N1"]["verified"])
        self.assertEqual(len(output["verify"]["N1"]["failed_jobs"]), 1)

    def test_too_few_jobs_or_wrong_rate_is_not_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 29)
            output = survey.aggregate(Path(tmp), "verify")
        self.assertTrue(output["verify"]["N1"]["all_pass"])
        self.assertFalse(output["verify"]["N1"]["verified"])

        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 30, rate=5000)
            output = survey.aggregate(Path(tmp), "verify", nominal={"N1": 4500})
        self.assertFalse(output["verify"]["N1"]["verified"])

    def test_thirty_jobs_on_one_version_only_is_not_verified(self):
        # 律速の版（windows-2022）を一度も検証していない。
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 30, versions=("windows-2025",))
            output = survey.aggregate(Path(tmp), "verify", nominal={"N1": 4500})

        entry = output["verify"]["N1"]
        self.assertTrue(entry["all_pass"])
        self.assertFalse(entry["verified"])
        self.assertTrue(any("windows-2022 のジョブ数 0" in note for note in entry["notes"]))
        self.assertTrue(survey.verdict_is_negative(output))

    def test_thirty_jobs_split_across_versions_is_not_verified(self):
        # K = 30 は版あたり。両版合計で 30（15 + 15）では足りない。
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 15)
            output = survey.aggregate(Path(tmp), "verify")

        self.assertEqual(output["verify"]["N1"]["jobs"], 30)
        self.assertFalse(output["verify"]["N1"]["verified"])

    def test_steps_shorter_than_the_minimum_duration_are_not_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), 30, duration=60)
            output = survey.aggregate(Path(tmp), "verify", nominal={"N1": 4500})

        entry = output["verify"]["N1"]
        self.assertTrue(entry["all_pass"])
        self.assertFalse(entry["verified"])
        self.assertTrue(any("300 秒未満" in note for note in entry["notes"]))

    def test_job_with_missing_json_fails_without_a_bogus_duration(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.build(root, 30)
            write_sample(root, "w22-broken", "windows-2022", [], phase="verify",
                         ladder_log=[{"Series": "N1", "Value": 4500, "Json": None, "ExitCode": 1}])
            output = survey.aggregate(root, "verify")

        entry = output["verify"]["N1"]
        self.assertFalse(entry["verified"])
        self.assertEqual(entry["min_duration_seconds"], 300)
        self.assertFalse(any("秒未満" in note for note in entry["notes"]))


class AggregateFlowControlTests(unittest.TestCase):
    def build(self, root: Path, fired: int = 0, overridden: bool = False):
        for i in range(3):
            write_sample(root, f"fc-{i}", "windows-2025", [
                make_report(rate=4500, flow_control_enabled=True, flow_control_overridden=overridden,
                            flow_control_dropped=fired if i == 0 else 0, accounted_loss=fired if i == 0 else 0),
                make_report(scenario="BurstQ1Drop", burst_count=2000, flow_control_enabled=True, flow_control_overridden=overridden),
            ], phase="flowcontrol")

    def test_no_firing_under_default_configuration(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp))
            output = survey.aggregate(Path(tmp), "flowcontrol")

        self.assertTrue(output["flowcontrol"]["no_firing"])
        self.assertEqual(output["flowcontrol"]["by_series"]["N1"]["runs"], 3)
        self.assertEqual(output["flowcontrol"]["by_series"]["BURST"]["runs"], 3)

    def test_any_firing_is_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), fired=12)
            output = survey.aggregate(Path(tmp), "flowcontrol")

        self.assertFalse(output["flowcontrol"]["no_firing"])
        self.assertEqual(output["flowcontrol"]["by_series"]["N1"]["max_dropped"], 12)

    def test_overridden_flow_control_is_not_the_default_configuration(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.build(Path(tmp), overridden=True)
            output = survey.aggregate(Path(tmp), "flowcontrol")

        self.assertFalse(output["flowcontrol"]["no_firing"])
        self.assertTrue(output["flowcontrol"]["problems"])


class CliTests(unittest.TestCase):
    def test_judge_prints_one_json_line(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "r.json"
            path.write_text(json.dumps(make_report()), encoding="utf-8")
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                code = survey.main(["judge", str(path)])

        self.assertEqual(code, 0)
        self.assertTrue(json.loads(buffer.getvalue())["pass"])

    def test_aggregate_writes_markdown_and_json_and_strict_exit(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "artifacts"
            AggregateVerifyTests.build(root, 3, failing=1)
            md = Path(tmp) / "summary.md"
            js = Path(tmp) / "summary.json"

            code = survey.main(["aggregate", str(root), "--phase", "verify", "--out-md", str(md), "--out-json", str(js), "--strict"])

            self.assertEqual(code, 1)
            self.assertIn("確定時の検証", md.read_text(encoding="utf-8"))
            self.assertEqual(json.loads(js.read_text(encoding="utf-8"))["verify"]["N1"]["jobs"], 6)

    def run_judge_with_ansi_code_page(self, report: dict) -> subprocess.CompletedProcess:
        """Windows ランナーの Python がパイプへ書くときと同じく、標準出力を cp1252 にして judge を実行する。"""
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "r.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            env = {k: v for k, v in os.environ.items() if k != "PYTHONUTF8"}
            env["PYTHONIOENCODING"] = "cp1252"
            return subprocess.run([sys.executable, str(SCRIPT), "judge", str(path)],
                                  capture_output=True, env=env, timeout=60)

    def test_judge_output_survives_a_cp1252_stdout(self):
        # 不合格の段は日本語の reasons を持つ。ASCII で出さないと UnicodeEncodeError で exit 1 になり、
        # workflow の Measure ステップが最初の不合格段で打ち切られる。
        completed = self.run_judge_with_ansi_code_page(make_report(accounted_loss=100, spool_evacuated=3))

        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        lines = completed.stdout.decode("ascii").splitlines()
        self.assertEqual(len(lines), 1)
        verdict = json.loads(lines[0])
        self.assertFalse(verdict["pass"])
        self.assertIn("計上済みの破棄 100 件", verdict["reasons"])
        self.assertIn("スプール退避 3 件", verdict["reasons"])

    def test_judge_rejection_of_a_non_report_is_ascii_too(self):
        completed = self.run_judge_with_ansi_code_page({"not": "a report"})

        self.assertEqual(completed.returncode, 2, completed.stderr.decode("utf-8", "replace"))
        verdict = json.loads(completed.stdout.decode("ascii"))
        self.assertEqual(verdict["reasons"], ["ScenarioReport 形式ではない"])


if __name__ == "__main__":
    unittest.main()
