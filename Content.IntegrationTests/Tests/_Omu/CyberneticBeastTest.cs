using System.Linq;
using Content.Goobstation.Shared.Overlays;
using Content.Goobstation.Common.Speech;
using Content.Server.Traits;
using Content.Shared._Omu.Clothing.Components;
using Content.Shared._Omu.Traits;
using Content.Shared.Atmos.Components;
using Content.Shared.Body.Components;
using Content.Shared.Clothing;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Omu;

[TestFixture]
public sealed class CyberneticBeastTest
{
    /// <summary>
    /// Other clients receive the wearer's eye color and use it for the equipped mantle.
    /// </summary>
    [Test]
    public async Task MantleEyeColorReplicatesToClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        EntityUid mob = default, mantle = default;
        var color = Color.FromHex("#ff3366");
        await server.WaitAssertion(() =>
        {
            mob = server.EntMan.SpawnEntity("MobVulpkanin", map.GridCoords);
            var appearance = server.EntMan.GetComponent<HumanoidAppearanceComponent>(mob);
            appearance.EyeColor = color;
            server.EntMan.Dirty(mob, appearance);
            server.EntMan.AddComponent<CyberneticBeastComponent>(mob);
            mantle = server.EntMan.SpawnEntity("ClothingCyberneticBeastMantle", map.GridCoords);
            Assert.That(server.System<InventorySystem>().TryEquip(mob, mantle, "head"), Is.True);
            server.PlayerMan.SetAttachedEntity(pair.Player!, mob);
        });
        await pair.RunTicksSync(20);
        var clientMob = pair.ToClientUid(mob);
        var clientMantle = pair.ToClientUid(mantle);
        await client.WaitAssertion(() =>
        {
            var appearance = client.System<Robust.Client.GameObjects.AppearanceSystem>();
            Assert.That(appearance.TryGetData<Color>(clientMantle, CyberneticMantleVisuals.EyeColor, out var received), Is.True);
            Assert.That(received.R, Is.EqualTo(color.R).Within(0.00001f));
            Assert.That(received.G, Is.EqualTo(color.G).Within(0.00001f));
            Assert.That(received.B, Is.EqualTo(color.B).Within(0.00001f));
            var visuals = new GetEquipmentVisualsEvent(clientMob, "head");
            client.EntMan.EventBus.RaiseLocalEvent(clientMantle, visuals);
            Assert.That(visuals.Layers, Is.Not.Empty);
            Assert.That(visuals.Layers[^1].Item2.Color, Is.EqualTo(received));
        });
        await server.WaitPost(() =>
        {
            server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(20);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Trait selection excludes conflicting eyesight traits and the server rejects non-Vulpkanin.
    /// </summary>
    [Test]
    public async Task TraitSelectionAndSpeciesRestriction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Vulpkanin")
                .WithTraitPreference("PoorVision", prototypes)
                .WithTraitPreference("CyberneticBeast", prototypes);
            Assert.That(profile.TraitPreferences.Select(id => id.Id), Does.Contain("CyberneticBeast").And.Not.Contain("PoorVision"));
            profile = profile.WithTraitPreference("Blindness", prototypes);
            Assert.That(profile.TraitPreferences.Select(id => id.Id), Does.Contain("Blindness").And.Not.Contain("CyberneticBeast"));
            var human = server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            server.System<TraitSystem>().ApplyTraits(human,
                HumanoidCharacterProfile.DefaultWithSpecies("Human").WithTraitPreference("CyberneticBeast", prototypes));
            Assert.That(server.EntMan.HasComponent<CyberneticBeastComponent>(human), Is.False);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Every supported job receives its original mantle, with a generic fallback for other jobs.
    /// </summary>
    [Test]
    public async Task TraitDeliversJobSpecificMantles()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var trait = prototypes.Index<TraitPrototype>("CyberneticBeast");
            var profile = HumanoidCharacterProfile.DefaultWithSpecies("Vulpkanin").WithTraitPreference("CyberneticBeast", prototypes);
            foreach (var (job, gear) in trait.TraitGearByJob)
            {
                Assert.That(prototypes.HasIndex(job), Is.True, $"Unknown job: {job}");
                var mob = server.EntMan.SpawnEntity("MobVulpkanin", map.GridCoords);
                server.System<TraitSystem>().ApplyTraits(mob, profile, job.Id);
                Assert.That(server.EntMan.HasComponent<CyberneticBeastComponent>(mob), Is.True);
                var held = server.System<Content.Server.Hands.Systems.HandsSystem>().EnumerateHeld(mob).ToArray();
                Assert.That(held.Any(item => server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == gear.Id), Is.True, $"Missing {gear} for {job}");
                server.EntMan.DeleteEntity(mob);
            }
            var fallback = server.EntMan.SpawnEntity("MobVulpkanin", map.GridCoords);
            server.System<TraitSystem>().ApplyTraits(fallback, profile, "Passenger");
            Assert.That(server.System<Content.Server.Hands.Systems.HandsSystem>().EnumerateHeld(fallback)
                .Any(item => server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "ClothingCyberneticBeastMantle"), Is.True);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(2);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Mantle powers require the trait; space variants connect internals, and removal frees both blocked slots.
    /// </summary>
    [TestCase("ClothingCyberneticBeastMantle", false)]
    [TestCase("ClothingCyberneticBeastMantleWork", true)]
    [TestCase("ClothingCyberneticBeastSpaceMantleSecurity", true)]
    [TestCase("ClothingCyberneticBeastSpaceMantleCorpsman", true)]
    public async Task EquipmentPowersSlotsAndInternals(string mantlePrototype, bool supportsInternals)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var inventory = server.System<InventorySystem>();
            var beast = entities.SpawnEntity("MobVulpkanin", map.GridCoords);
            entities.AddComponent<CyberneticBeastComponent>(beast);
            var mantle = entities.SpawnEntity(mantlePrototype, map.GridCoords);
            var glasses = entities.SpawnEntity("ClothingEyesGlasses", map.GridCoords);
            Assert.That(inventory.TryEquip(beast, glasses, "eyes"), Is.True);
            Assert.That(inventory.TryEquip(beast, mantle, "head"), Is.False);
            Assert.That(entities.HasComponent<NightVisionComponent>(mantle), Is.False, "A failed equip must not activate powers.");
            Assert.That(inventory.TryUnequip(beast, "eyes"), Is.True);
            Assert.That(inventory.TryEquip(beast, mantle, "head"), Is.True);
            Assert.That(entities.GetComponent<ItemToggleComponent>(mantle).Activated, Is.True);
            Assert.That(entities.HasComponent<NightVisionComponent>(mantle), Is.True);
            var voice = new GetSpeechSoundEvent();
            entities.EventBus.RaiseLocalEvent(beast, ref voice);
            Assert.That(voice.Handled, Is.True);
            Assert.That(voice.SpeechSoundProtoId, Is.EqualTo("Pai"));
            foreach (var slot in new[] { "mask", "eyes" })
            {
                Assert.That(inventory.TryGetSlotEntity(beast, slot, out var blocker), Is.True);
                Assert.That(entities.GetComponent<VirtualItemComponent>(blocker!.Value).BlockingEntity, Is.EqualTo(mantle));
            }
            if (supportsInternals)
            {
                Assert.That(entities.HasComponent<BreathToolComponent>(mantle), Is.True);
                Assert.That(entities.GetComponent<InternalsComponent>(beast).BreathTools, Does.Contain(mantle));
            }
            Assert.That(inventory.TryUnequip(beast, "head"), Is.True);
            Assert.That(entities.HasComponent<NightVisionComponent>(mantle), Is.False);
            voice = new GetSpeechSoundEvent();
            entities.EventBus.RaiseLocalEvent(beast, ref voice);
            Assert.That(voice.Handled, Is.False);
            foreach (var slot in new[] { "mask", "eyes" })
            {
                if (inventory.TryGetSlotEntity(beast, slot, out var blocker))
                    Assert.That(entities.IsQueuedForDeletion(blocker.Value), Is.True,
                        "Virtual inventory items are removed at the end of the tick.");
            }
            var human = entities.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(inventory.TryEquip(human, mantle, "head"), Is.True);
            Assert.That(entities.HasComponent<NightVisionComponent>(mantle), Is.False, "Ordinary wearers must not gain cybernetic powers.");
            Assert.That(inventory.TryUnequip(human, "head"), Is.True);
            entities.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(2);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// All imported mantle variants can spawn and be equipped and removed without stale equipment blockers.
    /// </summary>
    [Test]
    public async Task EveryMantleVariantCanBeEquipped()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        string[] variants = Array.Empty<string>();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            mob = entities.SpawnEntity("MobVulpkanin", map.GridCoords);
            entities.AddComponent<CyberneticBeastComponent>(mob);
            variants = prototypes.EnumeratePrototypes<EntityPrototype>()
                .Where(proto => !proto.Abstract && proto.Components.ContainsKey("CyberneticMantle"))
                .Select(proto => proto.ID).ToArray();
            Assert.That(variants.Length, Is.GreaterThanOrEqualTo(20));
        });
        foreach (var variant in variants)
        {
            await server.WaitAssertion(() =>
            {
                var inventory = server.System<InventorySystem>();
                foreach (var slot in new[] { "mask", "eyes" })
                    Assert.That(inventory.TryGetSlotEntity(mob, slot, out _), Is.False);
                var mantle = server.EntMan.SpawnEntity(variant, map.GridCoords);
                Assert.That(inventory.TryEquip(mob, mantle, "head"), Is.True, variant);
                Assert.That(server.EntMan.HasComponent<NightVisionComponent>(mantle), Is.True, variant);
                Assert.That(inventory.TryUnequip(mob, "head"), Is.True, variant);
                server.EntMan.DeleteEntity(mantle);
            });
            await pair.RunTicksSync(2);
        }
        await server.WaitPost(() => server.EntMan.DeleteEntity(map.MapUid));
        await pair.RunTicksSync(2);
        await pair.CleanReturnAsync();
    }
}
