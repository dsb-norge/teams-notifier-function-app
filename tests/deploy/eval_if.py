"""Evaluate reusable-deploy.yml's package-manifest `if:` over the outcomes that can happen.

Usage: eval_if.py '<the job's if: expression>'

Used by workflow.bats. The GitHub expression is translated term by term into Python and
evaluated for each case. A term the translation doesn't know raises NameError, so a changed
condition fails the test until the cases below are updated with it.
"""

import sys

TRANSLATION = (
    ("!cancelled()", "(not cancelled)"),
    ("&&", " and "),
    ("||", " or "),
    ("needs.preflight.outputs.reason", "reason"),
    ("needs.preflight.result", "preflight"),
    ("needs.deploy.result", "deploy"),
)

# (run cancelled, preflight result, deploy result, preflight's reason) -> should the job run?
CASES = (
    (False, "success", "success", "deploy", True),
    (False, "success", "skipped", "current", True),
    (False, "success", "skipped", "unreachable", False),
    (False, "success", "failure", "deploy", False),
    (False, "success", "cancelled", "deploy", False),
    (False, "failure", "skipped", "", False),
    (True, "success", "cancelled", "deploy", False),
    (True, "success", "skipped", "current", False),
)


def main(expression: str) -> int:
    for github, python in TRANSLATION:
        expression = expression.replace(github, python)
    bad = []
    for cancelled, preflight, deploy, reason, want in CASES:
        names = {"cancelled": cancelled, "preflight": preflight, "deploy": deploy, "reason": reason}
        got = eval(expression, {"__builtins__": {}}, names)  # noqa: S307 - our own workflow file
        if got is not want:
            bad.append(
                f"cancelled={cancelled} preflight={preflight} deploy={deploy} reason={reason}: "
                f"got {got}, want {want}"
            )
    print("\n".join(bad) or "all cases hold")
    return 1 if bad else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
