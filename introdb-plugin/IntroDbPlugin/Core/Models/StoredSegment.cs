// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Core.Models;

public sealed record StoredSegment(
    string Type,
    long StartMs,
    long EndMs,
    double Confidence);
