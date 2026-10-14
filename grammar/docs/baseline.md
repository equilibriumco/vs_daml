# Baseline: the real-world corpus parsed by the grammar

Generated 2026-10-14 by `grammar/scripts/parse-report.mjs` with tree-sitter CLI 0.27.1, grammar at `02cf144`,
corpus collected 2026-10-14T09:29:34Z (`grammar/test/real-world/manifest.json`).

An error inside a larger error is a consequence of it and isn't counted; a file's *first* error
is what stops it. A large error is placed at the first innermost error inside it, where the
parse actually went wrong, and named by the nearest Daml declaration or clause around that
place, read from the source (the grammar can't show a construct it doesn't know yet). Daml's
`:` for types, where Haskell has `::`, gets its own row: it is under most of the other constructs.

| | |
|---|---|
| Files | 764 |
| Parse without errors | 127 (16.6%) |
| With errors | 637 |
| Error regions | 19639 |

## By construct

| Construct | Files it stops first | Error regions | Task |
|---|---:|---:|---|
| type signature with `:` | 225 | 4904 | before M4-T04: Daml's `:`/`::` |
| record with expression | 138 | 12922 | M4-T07 |
| interface | 83 | 96 | M4-T06 |
| template clause: signatory | 67 | 166 | M4-T04 |
| other | 41 | 959 | to look at |
| interface instance | 28 | 214 | M4-T06 |
| choice | 16 | 176 | M4-T05 |
| template | 12 | 29 | M4-T04 |
| template clause: observer | 8 | 37 | M4-T04 |
| template clause: ensure | 7 | 87 | M4-T04 |
| contract key | 7 | 24 | M4-T07 (lower priority) |
| record with declaration | 5 | 19 | M4-T07 |
| exception | 0 | 6 | M4-T07 (lower priority) |

## By source

| Source | Files | Without errors |
|---|---:|---:|
| daml | 258 | 74 (28.7%) |
| daml-finance | 312 | 44 (14.1%) |
| samples | 9 | 3 (33.3%) |
| splice | 185 | 6 (3.2%) |

## Errors outside the Daml constructs

By the first word of the line the error is on, with one example each.

| Line starts with | Error regions | Example |
|---|---:|---|
| `let` | 96 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Generic/Test/Intermediated/BondCoupon.daml:126 |
| `expectedProduced` | 34 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1509 |
| `Some` | 32 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Generic/Test/Intermediated/BondCoupon.daml:410 |
| `data` | 27 | daml/sdk/compiler/damlc/daml-prim-src/GHC/Tuple.daml:45 |
| `->` | 20 | splice/token-standard/splice-token-standard-utils/daml/Splice/TokenStandard/Utils/Internal/Allocations.daml:218 |
| `MulF` | 16 | daml-finance/docs/generated/src/main/daml/ContingentClaims/Valuation/V0/MathML.daml:69 |
| `now` | 13 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Generic/Test/Intermediated/BondCoupon.daml:179 |
| `businessDayConvention` | 12 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1557 |
| `calendarCid` | 12 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1479 |
| `currencies` | 12 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1476 |
| `import` | 12 | daml/sdk/docs/sharable/sdk/component-howtos/smart-contracts/daml-studio/daml/ExerciseAuthFailure.daml:7 |
| `swapStreams` | 12 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1475 |
| `[custodian,` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1536 |
| `calendar` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1564 |
| `clientPartyRef` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1577 |
| `issueDate` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1547 |
| `issuerPartyRef` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1576 |
| `maturityDate` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1548 |
| `primaryBusinessCenters` | 11 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1563 |
| `ccy` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1552 |
| `fixingCalendarCid` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1483 |
| `notional` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1551 |
| `observableCid` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1718 |
| `observations` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1558 |
| `referenceRateId` | 10 | daml-finance/docs/generated/src/test/daml/Daml/Finance/Instrument/Swap/Test/Fpml.daml:1549 |
