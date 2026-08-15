# P2 Final UI Hotfix 1

Fixes the WinForms startup exception caused by assigning `Color.Transparent`
to `ButtonBase.FlatAppearance.BorderColor` in the workspace navigation rail.

The unselected navigation border now uses the opaque navigation background
colour while `BorderSize` remains zero, preserving the intended appearance.

Core, persistence and test projects are unchanged.
