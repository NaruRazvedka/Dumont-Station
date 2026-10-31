// SPDX-FileCopyrightText: 2025 MarkerWicker (OmuStation)
// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from OmuStation PRs #150 and #1613; adapted for Dumont.

using Content.Shared._Omu.Clothing.EntitySystems;
using Robust.Shared.GameStates;

namespace Content.Shared._Omu.Clothing.Components;

/// <summary>
///     This component indicates that a clothing garment takes up multiple slots.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(ClothingTakesUpExtraSlotsSystem))]
public sealed partial class ClothingTakesUpExtraSlotsComponent : Component
{
    /// <summary>
    /// Inventory slots which the garment should take up, not including it's main slot.
    /// </summary>
    [DataField(required: true)]
    public string[] Slots;
}
