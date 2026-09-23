"""Tests unitaires des oracles — stdlib, sans réseau. `python3 -m unittest discover -s eval/tests`"""
import json, os, shutil, subprocess, sys, tempfile, unittest
HERE = os.path.dirname(os.path.abspath(__file__)); EVAL = os.path.dirname(HERE); PROJECT = os.path.dirname(EVAL)
sys.path.insert(0, EVAL)
import run_eval, run_tasks_eval, jsonschema_lite, check_tool_schemas  # noqa: E402


class RetrievalOracle(unittest.TestCase):
    def test_score_multi_doc_partial(self):
        item = {"id": "q", "expected_doc_ids": ["A", "B"], "must_contain": ["x"]}
        m = run_eval.score_one(item, [{"docId": "A", "score": 0.9, "text": "x"}, {"docId": "C", "score": 0.5}], 5)
        self.assertEqual(m["recall"], 0.5); self.assertEqual(m["all_hit"], 0.0); self.assertEqual(m["mrr"], 1.0); self.assertEqual(m["facts"], 1.0)

    def test_negative_uses_top_score(self):
        m = run_eval.score_one({"id": "q", "expected_doc_ids": [], "answerable": False}, [{"docId": "A", "score": 0.7}], 5)
        self.assertIsNone(m["recall"]); self.assertEqual(m["top_score"], 0.7)
        s = run_eval.summarize([m], neg_threshold=0.5); self.assertEqual(s["negative_ok"], 0.0)

    def test_compare_flags_only_drops_beyond_tolerance(self):
        regs = run_eval.compare({"recall": 0.80, "mrr": 0.99}, {"recall": 0.85, "mrr": 1.0}, 0.02)
        self.assertEqual([r["metric"] for r in regs], ["recall"])

    def test_cli_from_file_offline(self):
        with tempfile.TemporaryDirectory() as d:
            code = run_eval.main(["--from-file", os.path.join(EVAL, "fixtures", "results_retrieval.jsonl"), "--runs-dir", d, "--quiet"])
            self.assertIn(code, (0, 1)); self.assertEqual(len(os.listdir(d)), 1)

    def test_cli_missing_file_is_exit_2(self):
        self.assertEqual(run_eval.main(["--from-file", "/nonexistent.jsonl", "--quiet"]), 2)


class TasksOracle(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.contract, cls.tools = run_tasks_eval.load_schemas(os.path.join(EVAL, "schemas"))
        cls.policies = run_tasks_eval.load_json(os.path.join(EVAL, "policy", "agents.json"))
        cls.golden = {t["id"]: t for t in run_tasks_eval.load_jsonl(os.path.join(EVAL, "tasks_golden.jsonl"))}
        cls.runs = {r["task_id"]: r for r in run_tasks_eval.load_jsonl(os.path.join(EVAL, "fixtures", "runs_tasks.jsonl"))}

    def failed(self, tid, run=None):
        A, _ = run_tasks_eval.check_task(self.golden[tid], run or self.runs[tid], self.contract, self.tools, self.policies)
        return [a["name"] for a in A if not a["ok"]]

    def test_good_run_passes(self):
        self.assertEqual(self.failed("T-01"), [])

    def test_missing_provenance_fails(self):
        self.assertIn("provenance_cites:DOC-PROC-REFABRICATION-BADGE", self.failed("T-03"))

    def test_side_effect_without_idempotency_key_fails(self):
        self.assertIn("idempotency_key[0]:creer_demande_badge", self.failed("T-06"))

    def test_tool_outside_allowlist_is_caught(self):
        run = dict(self.runs["T-01"]); run["tool_calls"] = [{"tool": "supprimer_compte", "input": {}, "output": {}, "ok": True}]
        self.assertIn("policy_allowlist", self.failed("T-01", run))

    def test_removed_property_breaks_tool_io(self):
        run = json.loads(json.dumps(self.runs["T-01"])); run["tool_calls"][0]["input"].pop("query")
        self.assertTrue(any(n.startswith("tool_io_valid[0]") for n in self.failed("T-01", run)))

    def test_contract_rejects_unknown_field(self):
        run = dict(self.runs["T-01"]); run["internal_spans"] = []
        self.assertIn("contract_valid", self.failed("T-01", run))

    def test_kappa_and_agreement(self):
        self.assertEqual(run_tasks_eval.kappa([(True, True), (False, False)]), 1.0)
        self.assertEqual(run_tasks_eval.kappa([(True, False), (False, True)]), -1.0)

    def test_cli_with_judge_file(self):
        with tempfile.TemporaryDirectory() as d:
            code = run_tasks_eval.main(["--runs", os.path.join(EVAL, "fixtures", "runs_tasks.jsonl"), "--judge-file", os.path.join(EVAL, "fixtures", "judge.jsonl"), "--runs-dir", d, "--quiet"])
            self.assertIn(code, (0, 1))
            out = json.load(open(os.path.join(d, os.listdir(d)[0]), encoding="utf-8"))
            self.assertEqual(out["metrics"]["n_judged"], 10); self.assertIsNotNone(out["metrics"]["agreement"])

    def test_judge_cmd_contract(self):
        cmd = f"{sys.executable} -c \"import sys,json; d=json.load(sys.stdin); print(json.dumps({{'pass': 'rubric' in d, 'reason': 'ok'}}))\""
        with tempfile.TemporaryDirectory() as d:
            self.assertIn(run_tasks_eval.main(["--runs", os.path.join(EVAL, "fixtures", "runs_tasks.jsonl"), "--judge-cmd", cmd, "--runs-dir", d, "--quiet"]), (0, 1))


class ToolIsAType(unittest.TestCase):
    def test_schema_drift_detected_when_type_changes(self):
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, "src", "Nexus.Tools", "Contracts"); shutil.copytree(os.path.join(PROJECT, "src", "Nexus.Tools", "Contracts"), src)
            sch = os.path.join(d, "schemas"); shutil.copytree(os.path.join(EVAL, "schemas", "tools"), sch)
            cs = os.path.join(src, "RechercherDocuments.cs"); txt = open(cs, encoding="utf-8").read()
            open(cs, "w", encoding="utf-8").write(txt.replace("int K = 5", "int K = 7"))
            problems, _ = check_tool_schemas.check(sch, os.path.join(EVAL, "tasks_golden.jsonl"), src, False)
            # x-source est relatif au projet : la source ne bouge pas là, mais la régénération diverge
            self.assertTrue(any(p["kind"] == "drift" for p in problems))

    def test_golden_references_must_exist(self):
        self.assertTrue(jsonschema_lite.property_exists(self.schema("rechercher_documents")["output"], "results.doc_id"))
        self.assertFalse(jsonschema_lite.property_exists(self.schema("rechercher_documents")["input"], "page"))

    def schema(self, name):
        return json.load(open(os.path.join(EVAL, "schemas", "tools", f"{name}.schema.json"), encoding="utf-8"))


# Les hooks vivent sous _horus/claude/ et non _horus/.claude/ : un dossier
# nomme .claude a l'interieur de la quarantaine risquerait d'etre charge,
# et c'est precisement ce que le LISEZ-MOI dit d'eviter.
@unittest.skipUnless(shutil.which("jq") and shutil.which("sh"), "sh + jq requis")
class Guardrails(unittest.TestCase):
    def hook(self, script, args, payload):
        env = dict(os.environ, CLAUDE_PROJECT_DIR=PROJECT)
        return subprocess.run(["sh", os.path.join(PROJECT, "claude", script), *args], input=json.dumps(payload), capture_output=True, text=True, env=env, cwd=PROJECT).returncode

    def test_all_probes(self):
        for pr in run_tasks_eval.load_jsonl(os.path.join(EVAL, "guardrail_probes.jsonl")):
            if pr["kind"] == "hook":
                with self.subTest(pr["id"]):
                    self.assertEqual(self.hook(os.path.basename(pr["hook"]), pr.get("args", []), pr["input"]), pr["expect_exit"], pr["danger"])


if __name__ == "__main__":
    unittest.main()
