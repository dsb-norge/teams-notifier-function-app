# Azure Monitor common alert schema samples

The alert tests run against the payloads Microsoft documents for the
[common alert schema](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-common-schema).
Microsoft publishes no usable machine-readable schema for it. The request schema in the
[Logic Apps guide](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-logic-apps)
covers `essentials` only, leaves `alertContext` as `{}`, and types fields as strings that
the samples send as null (budget alerts have no `alertRule`, `severity` or `signalType`).
What Microsoft does publish is the field tables and the sample payloads, so this folder
holds those:

| Path | What it is |
|---|---|
| `samples/payload-samples/` | Every alert in [Sample alert payloads](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-payload-samples), including the payloads an action group's **Test** button sends (`test-action-*`). |
| `samples/common-schema/` | The samples on the [Common alert schema](https://learn.microsoft.com/azure/azure-monitor/alerts/alerts-common-schema) page that differ from the ones above. |
| `manifest.json` | The source commit, each sample's heading and line in the docs, and the field names of the docs' **Essentials fields** table. |
| `LICENSE-CODE` | The MIT License the samples are under, copied from the source repository. |

A sample that is only an `alertContext` fragment is wrapped in the essentials of the docs'
generic sample by `AzureMonitorAlertSamples.Payload`, so every sample can go through the
alert function.

## Provenance and licence

Copied verbatim from [MicrosoftDocs/azure-monitor-docs](https://github.com/MicrosoftDocs/azure-monitor-docs)
at the commit named in `manifest.json`. The code samples in that repository are licensed
under the MIT License, Copyright (c) Microsoft Corporation, and the licence asks for its
notice to go with every copy: `LICENSE-CODE` here is that notice, fetched from the same
commit. The documentation text is CC BY 4.0. The addresses, GUIDs and names in the samples are
Microsoft's: placeholders such as `contoso.com`, `sample.test` and `aaaa0a0a-…`, and the
public DNS resolver `1.1.1.1` as a dimension value in one sample. None of it is ours.

## Refreshing

Don't edit the files by hand. From the repository root:

```bash
python3 -I scripts/fetch-alert-samples.py             # re-fetch at the pinned commit
python3 -I scripts/fetch-alert-samples.py --ref main  # move the pin to the docs' current main
```

The script downloads everything first, then replaces the old samples, the manifest and
`LICENSE-CODE` and, with `--ref`, updates the pin in itself. A failed download changes
nothing. Commit the script and this folder together, then run the tests. A new essentials
field fails `CommonAlertPayloadTests` until it is either modelled or added to the list of
fields the card deliberately leaves out; a new sample shape that breaks the card fails
`AlertFunctionSampleTests`.
