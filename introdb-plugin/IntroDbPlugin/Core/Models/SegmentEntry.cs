// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroDbPlugin.Core.Models;

public sealed record SegmentEntry(
    double StartSec,
    double EndSec,
    long StartMs,
    long EndMs,
    double Confidence,
    int SubmissionCount,
    DateTimeOffset? UpdatedAt);
