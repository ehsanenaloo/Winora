// SPDX-License-Identifier: GPL-3.0-only
// Thin composition root of the elevated worker. Everything testable lives in Winora.Elevation.Windows (Worker/**).
// With no arguments this does nothing and returns exit code 3, so a plain launch of the shipped binary is inert.
return Winora.Elevation.Windows.Worker.WorkerEntry.Run(args);
