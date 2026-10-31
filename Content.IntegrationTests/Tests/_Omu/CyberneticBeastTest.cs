using System.Linq;
using Content.Goobstation.Shared.Overlays;
using System.Numerics;
using Robust.Shared.ComponentTrees;
using Robust.Shared.Light;
using Robust.Shared.Map;
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
    /// The glow stays in front of the wearer's face when turning in any cardinal direction.
    /// </summary>
    [TestCase("ClothingCyberneticBeastMantle")]
    [TestCase("ClothingCyberneticBeastMantleBlueshieldOfficer")]
    [TestCase("ClothingCyberneticBeastMantleWorkSalvage")]
    public async Task MantleGlowFollowsTheWearersFront(string prototype)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        EntityUid mob = default, mantle = default;
        await server.WaitAssertion(() =>
        {
            mob = server.EntMan.SpawnEntity("MobVulpkanin", map.GridCoords);
            server.EntMan.AddComponent<CyberneticBeastComponent>(mob);
            mantle = server.EntMan.SpawnEntity(prototype, map.GridCoords);
            Assert.That(server.System<InventorySystem>().TryEquip(mob, mantle, "head"), Is.True);
            var mapId = server.System<SharedTransformSystem>().GetMapCoordinates(mob).MapId;
            server.System<SharedMapSystem>().SetAmbientLight(mapId, Color.Black);
            server.PlayerMan.SetAttachedEntity(pair.Player!, mob);
        });
        await pair.RunTicksSync(20);
        var clientMob = pair.ToClientUid(mob);
        var clientMantle = pair.ToClientUid(mantle);
        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            await server.WaitPost(() => server.System<SharedTransformSystem>().SetWorldRotation(mob, Angle.FromDegrees(degrees)));
            await pair.RunTicksSync(10);
            await client.WaitAssertion(() =>
            {
                var transform = client.System<SharedTransformSystem>();
                var position = transform.GetWorldPosition(clientMob);
                var rotation = transform.GetWorldRotation(clientMob);
                Assert.That(Math.Abs(Angle.ShortestDistance(rotation, Angle.FromDegrees(degrees)).Theta), Is.LessThan(0.001), "The test must cover every cardinal direction.");
                var forward = rotation.ToWorldVec();
                Assert.That(client.System<Robust.Client.GameObjects.PointLightSystem>().TryGetLight(clientMantle, out var light), Is.True);
                var lightCenter = Vector2.Transform(light!.Offset, transform.GetWorldMatrix(clientMantle));
                Assert.That(Vector2.Dot(lightCenter - position, forward), Is.EqualTo(0.2f).Within(0.001f),
                    $"{prototype} must emit slightly ahead of the wearer at {degrees} degrees.");
                client.System<SharedLightTreeSystem>().FrameUpdate(0);
                var mapId = client.EntMan.GetComponent<TransformComponent>(clientMob).MapID;
                var levels = client.System<LightLevelSystem>();
                Assert.That(levels.TryCalculateLightColor(new MapCoordinates(position + forward * 0.4f, mapId), out var front), Is.True);
                Assert.That(levels.TryCalculateLightColor(new MapCoordinates(position - forward * 0.4f, mapId), out var back), Is.True);
                Assert.That(front.R + front.G + front.B, Is.GreaterThan(back.R + back.G + back.B),
                    "The ambient glow must be stronger in front of the face than behind the back.");
            });
        }
        await server.WaitPost(() =>
        {
            server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(20);
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Equipped mantles retain their original glow and replicate their eye and light color to clients.
    /// </summary>
    [TestCase("ClothingCyberneticBeastMantle", 1.45f, 0.6f)]
    [TestCase("ClothingCyberneticBeastMantleCentralCommandNoThermal", 1.45f, 0.6f)]
    [TestCase("ClothingCyberneticBeastMantleWorkSalvage", 5f, 0.45f)]
    public async Task MantleColorAndGlowReplicateToClient(string prototype, float radius, float energy)
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
            mantle = server.EntMan.SpawnEntity(prototype, map.GridCoords);
            Assert.That(server.System<InventorySystem>().TryEquip(mob, mantle, "head"), Is.True);
            Assert.That(server.System<SharedPointLightSystem>().TryGetLight(mantle, out var light), Is.True);
            Assert.That(light!.Enabled, Is.True);
            Assert.That(light.ContainerOccluded, Is.False, "Equipped mantles must not suppress their light.");
            Assert.That(light.Radius, Is.EqualTo(radius).Within(0.0001f));
            Assert.That(light.Energy, Is.EqualTo(energy).Within(0.0001f));
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
            var lights = client.System<Robust.Client.GameObjects.PointLightSystem>();
            Assert.That(lights.TryGetLight(clientMantle, out var light), Is.True);
            Assert.That(light!.AddToTree, Is.True, "The client must be able to render the equipped mantle's glow.");
            Assert.That(light.Radius, Is.EqualTo(radius).Within(0.0001f));
            Assert.That(light.Energy, Is.EqualTo(energy).Within(0.0001f));
            Assert.That(light.Color.R, Is.EqualTo(received.R).Within(0.00001f));
            Assert.That(light.Color.G, Is.EqualTo(received.G).Within(0.00001f));
            Assert.That(light.Color.B, Is.EqualTo(received.B).Within(0.00001f));
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
            Assert.That(trait.TraitGearByJob["SalvageLead"].Id, Is.EqualTo("ClothingCyberneticBeastMantleWorkSalvage"));
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
    [TestCase("ClothingCyberneticBeastMantleHeadofSecurity", true)]
    [TestCase("ClothingCyberneticBeastMantleMedicalCMO", true)]
    [TestCase("ClothingCyberneticBeastMantleCommand", true)]
    [TestCase("ClothingCyberneticBeastMantleSupply", true)]
    [TestCase("ClothingCyberneticBeastMantleScienceCommand", true)]
    [TestCase("ClothingCyberneticBeastMantleCentralCommandNoThermal", true)]
    [TestCase("ClothingCyberneticBeastMantleWorkChiefEngineering", true)]
    [TestCase("ClothingCyberneticBeastMantleBlueshieldOfficer", true)]
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
                var pressure = entities.GetComponent<Content.Server.Atmos.Components.PressureProtectionComponent>(mantle);
                Assert.That(pressure.HighPressureMultiplier, Is.LessThan(1));
                Assert.That(pressure.LowPressureMultiplier, Is.GreaterThan(1));
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
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        EntityUid mob = default;
        string[] variants = Array.Empty<string>();
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            mob = entities.SpawnEntity("MobVulpkanin", map.GridCoords);
            entities.AddComponent<CyberneticBeastComponent>(mob);
            var mapId = server.System<SharedTransformSystem>().GetMapCoordinates(mob).MapId;
            server.System<SharedMapSystem>().SetAmbientLight(mapId, Color.Black);
            server.PlayerMan.SetAttachedEntity(pair.Player!, mob);
            variants = prototypes.EnumeratePrototypes<EntityPrototype>()
                .Where(proto => !proto.Abstract && proto.Components.ContainsKey("CyberneticMantle"))
                .Select(proto => proto.ID).OrderBy(id => id).ToArray();
            Assert.That(variants.Length, Is.GreaterThanOrEqualTo(20));
        });

        async Task VerifyGlow(EntityUid mantle, string label)
        {
            await pair.RunTicksSync(20);
            var clientMantle = pair.ToClientUid(mantle);
            await client.WaitAssertion(() =>
            {
                var lights = client.System<Robust.Client.GameObjects.PointLightSystem>();
                Assert.That(lights.TryGetLight(clientMantle, out var light), Is.True, label);
                Assert.That(light!.AddToTree, Is.True, label);
                client.System<SharedLightTreeSystem>().FrameUpdate(0);
                var transform = client.System<SharedTransformSystem>();
                var center = Vector2.Transform(light.Offset, transform.GetWorldMatrix(clientMantle));
                var mapId = client.EntMan.GetComponent<TransformComponent>(clientMantle).MapID;
                var samplePoint = new MapCoordinates(center + new Vector2(0.4f, 0), mapId);
                Assert.That(client.System<LightLevelSystem>().TryCalculateLightColor(samplePoint, out var illumination), Is.True, label);
                var brightness = Math.Max(illumination.R, Math.Max(illumination.G, illumination.B));
                TestContext.Progress.WriteLine($"{label}: light={light.Color}, illumination={illumination}, radius={light.Radius}, energy={light.Energy}, falloff={light.Falloff}");
                Assert.That(brightness, Is.GreaterThanOrEqualTo(0.04f), $"Ambient glow is effectively invisible: {label}");
                Assert.That(brightness, Is.LessThanOrEqualTo(light.Energy + 0.001f), "The test map must not contribute ambient light.");
            });
        }

        var colors = new[] { Color.Red, Color.FromHex("#6600aa"), Color.Black };
        for (var i = 0; i < variants.Length; i++)
        {
            var variant = variants[i];
            EntityUid mantle = default;
            await server.WaitAssertion(() =>
            {
                var inventory = server.System<InventorySystem>();
                foreach (var slot in new[] { "mask", "eyes" })
                    Assert.That(inventory.TryGetSlotEntity(mob, slot, out _), Is.False);
                var humanoid = server.EntMan.GetComponent<HumanoidAppearanceComponent>(mob);
                humanoid.EyeColor = variant.Contains("Blueshield") ? colors[1] : colors[i % colors.Length];
                server.EntMan.Dirty(mob, humanoid);
                mantle = server.EntMan.SpawnEntity(variant, map.GridCoords);
                Assert.That(inventory.TryEquip(mob, mantle, "head"), Is.True, variant);
                Assert.That(server.EntMan.HasComponent<NightVisionComponent>(mantle), Is.True, variant);
            });
            await VerifyGlow(mantle, $"{variant} equipped");
            await server.WaitAssertion(() => Assert.That(server.System<InventorySystem>().TryUnequip(mob, "head"), Is.True, variant));
            await VerifyGlow(mantle, $"{variant} on ground");
            await server.WaitPost(() => server.EntMan.DeleteEntity(mantle));
            await pair.RunTicksSync(2);
        }
        await server.WaitPost(() =>
        {
            server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(20);
        await pair.CleanReturnAsync();
    }
}
