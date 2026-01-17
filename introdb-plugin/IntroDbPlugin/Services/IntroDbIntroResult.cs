// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Services;

/// <summary>
/// Intro timestamp result from IntroDB.
/// </summary>
/// <param name="ImdbId">IMDb id.</param>
/// <param name="Season">Season number.</param>
/// <param name="Episode">Episode number.</param>
/// <param name="StartSeconds">Intro start in seconds.</param>
/// <param name="EndSeconds">Intro end in seconds.</param>
/// <param name="Confidence">Confidence score.</param>
/// <param name="SubmissionCount">Number of submissions.</param>
public sealed record IntroDbIntroResult(
    string ImdbId,
    int Season,
    int Episode,
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    int SubmissionCount);
