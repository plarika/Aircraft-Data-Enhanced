# P2 Diagnostics Layout Hotfix

## Problem

The diagnostics panel could fail during plugin initialization with:

```text
Additional Rows or Columns cannot be created. TableLayoutPanel is full and GrowStyle is FixedSize.
```

The responsive resize handler reduced `ColumnCount` from two to one while six controls were still assigned to the original fixed-size 2x3 grid.

## Correction

- Captures and removes the metric cards before changing table dimensions.
- Temporarily switches the grid to `AddRows` during reconstruction.
- Rebuilds column and row styles before adding the cards back.
- Restores `FixedSize` only after the new layout is complete.
- Uses `SuspendLayout` / `ResumeLayout`.
- Adds a reentrancy guard and avoids rebuilding an already active layout.
- Applies the initial compact/wide layout explicitly during construction.

No decoder, IQ pipeline, persistence, aircraft lookup, or export code was changed.
