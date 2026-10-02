"""Validate a Teams app manifest against a JSON schema.

Usage: validate-manifest-schema.py <schema.json> <manifest.json>

Called by validate-manifest-schema.sh, which fetches the schema the manifest names in its
$schema field and installs jsonschema from the hash-pinned requirements.txt next to this file.
Prints GitHub workflow commands; exits non-zero on any schema error.
"""

import json
import sys

from jsonschema import validators


def escape(message: str) -> str:
    """Escape a workflow command message the way @actions/core does."""
    return message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def main(schema_path: str, manifest_path: str) -> int:
    with open(schema_path, encoding="utf-8") as f:
        schema = json.load(f)
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

    validator_class = validators.validator_for(schema)
    # Check the schema's structure against its meta-schema, but not its "format" values. The
    # Teams schema writes its patterns in ECMAScript syntax (\p{L}), which Python's re rejects,
    # and jsonschema >= 4.17 checks "format": "regex" in check_schema by default. The unpinned
    # install this replaced only passed because it resolved to the runner image's older
    # jsonschema. The manifest itself is validated without a format checker, as before.
    validator_class.check_schema(schema, format_checker=None)
    errors = sorted(
        validator_class(schema).iter_errors(manifest),
        key=lambda e: [str(p) for p in e.absolute_path],
    )
    if not errors:
        print(f"::notice title=Manifest validation::schema-valid against {escape(str(schema.get('$id', 'the Teams schema')))}")
        return 0

    print(f"::error::Teams manifest schema validation failed ({len(errors)} error(s)):")
    for e in errors:
        path = ".".join(str(p) for p in e.absolute_path) or "<root>"
        print(f"::error::  at {escape(path)}: {escape(e.message)}")
    return 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2]))
