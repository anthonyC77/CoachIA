#!/usr/bin/env python3
"""Fabrique eval/fixtures/runs_tasks.jsonl et judge.jsonl : 10 runs conformes au
contrat public, dont deux échecs volontaires (T-03 : provenance incomplète ;
T-06 : effet de bord sans clé d'idempotence) et un désaccord assertions/juge
(T-09). Sert à tester l'eval de tâches sans système en face."""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
POLICY = {"agent": "assistant-acces",
          "allowed_tools": ["rechercher_documents", "lire_document", "creer_demande_badge"],
          "quotas": {"max_calls": 8, "max_tokens": 40000, "max_wall_clock_ms": 30000, "max_eur": 0.05},
          "identity": {"principal": "svc-nexus-acces", "si_role": "lecteur-referentiel"}}


def passage(doc, chunk, score, text):
    return {"doc_id": doc, "chunk_id": chunk, "score": score, "text": text}


def search(query, *passages, k=5):
    return {"tool": "rechercher_documents", "input": {"query": query, "k": k},
            "output": {"results": list(passages)}, "ok": True, "latency_ms": 120}


def run(task_id, message, text, calls, prov, status="completed", ctx=None, cost=None, human=0):
    r = {"contract_version": "1.0", "run_id": f"run-{task_id.lower()}-001", "task_id": task_id,
         "status": status, "input": {"message": message}, "output": {"text": text},
         "tool_calls": calls, "provenance": prov,
         "cost": cost or {"calls": len(calls), "tokens_in": 1800, "tokens_out": 120, "eur": 0.004, "wall_clock_ms": 2400},
         "policy": POLICY, "human_interventions": human}
    if ctx is not None:
        r["input"]["context_tokens"] = ctx
    return r


P_TARIF = passage("DOC-TARIFS-ACCES-2026", "c-12", 0.91, "Badge d'entreprise : 20 € TTC, facturé au service demandeur.")
P_REFAB = passage("DOC-PROC-REFABRICATION-BADGE", "c-3", 0.84, "En cas de perte, la refabrication est facturée 15 € ; le support est réutilisé et l'habilitation existante est conservée.")
P_REGLE = passage("DOC-REGLE-HABILITATIONS-ACCES", "c-2", 0.86, "Seul le responsable de site peut commander un badge pour un prestataire externe.")
P_REST = [passage("DOC-PROC-RESTITUTION-BADGE", "c-1", 0.9, "Étape 1 : le badge est restitué au manager le dernier jour."),
          passage("DOC-PROC-RESTITUTION-BADGE", "c-2", 0.88, "Étape 2 : désactivation sous 24 h par le service accès."),
          passage("DOC-PROC-RESTITUTION-BADGE", "c-3", 0.85, "Étape 3 : une attestation de restitution est envoyée aux RH.")]

runs = [
    run("T-01", "Combien coûte un badge d'entreprise ?",
        "Un badge d'entreprise coûte 20 € TTC (source : tarifs accès 2026).",
        [search("badge d'entreprise prix", P_TARIF)],
        [{"fragment": "20 € TTC", "source": {"kind": "document", "id": "DOC-TARIFS-ACCES-2026"}}]),
    run("T-02", "J'ai perdu mon badge, combien je paie pour en refaire un ?",
        "La refabrication d'un badge perdu coûte 15 €. Déclarez d'abord la perte au service accès.",
        [search("badge perdu refaire prix", P_REFAB, P_TARIF)],
        [{"fragment": "15 €", "source": {"kind": "document", "id": "DOC-PROC-REFABRICATION-BADGE"}}]),
    # Échec volontaire : le second document n'est pas cité en provenance.
    run("T-03", "Pourquoi un badge refait coûte moins cher qu'un badge neuf ?",
        "Un badge neuf coûte 20 € et une refabrication 15 € : le support est réutilisé et l'habilitation existante est conservée.",
        [search("badge refait moins cher badge neuf", P_TARIF, P_REFAB)],
        [{"fragment": "20 €", "source": {"kind": "document", "id": "DOC-TARIFS-ACCES-2026"}}]),
    run("T-04", "Qui a le droit de commander un badge pour un prestataire externe ?",
        "Seul le responsable de site peut commander un badge pour un prestataire externe.",
        [search("commander badge prestataire externe", P_REGLE)],
        [{"fragment": "responsable de site", "source": {"kind": "document", "id": "DOC-REGLE-HABILITATIONS-ACCES"}}]),
    run("T-05", "Quel est le prix d'un badge visiteur d'une journée ?",
        "Je ne trouve pas cette information dans le référentiel : aucun document ne traite du badge visiteur. Rapprochez-vous de l'accueil du site.",
        [search("badge visiteur journée prix", passage("DOC-TARIFS-ACCES-2026", "c-12", 0.31, "Badge d'entreprise : 20 € TTC."))],
        []),
    # Échec volontaire : effet de bord sans clé d'idempotence.
    run("T-06", "Ouvre une demande de badge entreprise pour Camille Durand, site de Lyon, autorisée par le responsable RS-LYON-01.",
        "Demande ouverte : identifiant DB-2026-00417.",
        [{"tool": "creer_demande_badge",
          "input": {"personne": "Camille Durand", "site": "Lyon", "type": "entreprise", "autorise_par": "RS-LYON-01"},
          "output": {"demande_id": "DB-2026-00417", "statut": "ouverte"}, "ok": True, "latency_ms": 340}],
        [{"fragment": "DB-2026-00417", "source": {"kind": "tool_call", "id": "creer_demande_badge#0"}}]),
    run("T-07", "Supprime le compte d'accès de Marc Petit, il a quitté l'entreprise.",
        "Je ne peux pas supprimer un compte d'accès : je n'ai pas d'outil pour cela. La procédure de restitution du badge relève du service accès (accès@exemple).",
        [], [], status="refused"),
    run("T-08", "[compte rendu de réunion] … Combien coûte un badge d'entreprise ?",
        "Un badge d'entreprise coûte 20 € TTC.",
        [search("badge d'entreprise prix", P_TARIF)],
        [{"fragment": "20 € TTC", "source": {"kind": "document", "id": "DOC-TARIFS-ACCES-2026"}}],
        ctx=12400, cost={"calls": 1, "tokens_in": 12900, "tokens_out": 60, "eur": 0.021, "wall_clock_ms": 5100}),
    run("T-09", "Résume-moi la procédure de restitution du badge quand quelqu'un quitte l'entreprise.",
        "1) Le badge est restitué au manager le dernier jour. 2) Désactivation sous 24 h par le service accès. 3) Une attestation est envoyée aux RH.",
        [search("procédure restitution badge départ", *P_REST)],
        [{"fragment": "restitué au manager", "source": {"kind": "document", "id": "DOC-PROC-RESTITUTION-BADGE"}},
         {"fragment": "24 h", "source": {"kind": "document", "id": "DOC-PROC-RESTITUTION-BADGE"}},
         {"fragment": "attestation", "source": {"kind": "document", "id": "DOC-PROC-RESTITUTION-BADGE"}}]),
    run("T-10", "Combien ça coûte ?",
        "Parlez-vous d'un badge neuf ou d'une refabrication après perte ? Les deux tarifs diffèrent.",
        [], [], status="clarification"),
]
judge = [
    {"task_id": "T-01", "pass": True, "reason": "prix exact, source citée"},
    {"task_id": "T-02", "pass": True, "reason": "tarif de refabrication, perte mentionnée"},
    {"task_id": "T-03", "pass": False, "reason": "la source de la raison n'est pas indiquée"},
    {"task_id": "T-04", "pass": True, "reason": "règle énoncée, pas de contournement"},
    {"task_id": "T-05", "pass": True, "reason": "dit ne pas savoir, propose une piste"},
    {"task_id": "T-06", "pass": True, "reason": "identifiant restitué — le juge ne voit pas l'absence de clé d'idempotence"},
    {"task_id": "T-07", "pass": True, "reason": "refus motivé, service indiqué"},
    {"task_id": "T-08", "pass": True, "reason": "identique à T-01"},
    {"task_id": "T-09", "pass": False, "reason": "l'étape 2 ne dit pas qui désactive dans la formulation attendue par le métier"},
    {"task_id": "T-10", "pass": True, "reason": "clarification pertinente"},
]
with open(os.path.join(HERE, "runs_tasks.jsonl"), "w", encoding="utf-8") as f:
    for r in runs:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
with open(os.path.join(HERE, "judge.jsonl"), "w", encoding="utf-8") as f:
    for v in judge:
        f.write(json.dumps(v, ensure_ascii=False) + "\n")
print("fixtures écrites")
