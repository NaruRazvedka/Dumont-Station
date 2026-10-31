using Content.Goobstation.Common.CCVar;
using Content.Server.Examine;
using Content.Shared._Omu.Traits;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Omu;

[TestFixture]
public sealed class MantleExamineTest
{
    /// <summary>
    /// Detailed examination shows real clothing, while virtual slot blockers stay hidden.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task ExaminationHidesVirtualSlotBlockers(bool selfExamine)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        EntityUid wearer = default, examiner = default, mantle = default;
        await pair.Client.WaitPost(() =>
        {
            var config = pair.Client.ResolveDependency<IConfigurationManager>();
            config.SetCVar(GoobCVars.DetailedExamine, true);
            config.SetCVar(GoobCVars.LogInChat, false);
        });
        await server.WaitAssertion(() =>
        {
            wearer = server.EntMan.SpawnEntity("MobVulpkanin", map.GridCoords);
            server.EntMan.AddComponent<CyberneticBeastComponent>(wearer);
            mantle = server.EntMan.SpawnEntity("ClothingCyberneticBeastMantle", map.GridCoords);
            Assert.That(server.System<InventorySystem>().TryEquip(wearer, mantle, "head"), Is.True);
            examiner = selfExamine ? wearer : server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, examiner);
        });
        await pair.RunTicksSync(20);
        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var inventory = server.System<InventorySystem>();
            var text = server.System<ExamineSystem>().GetExamineText(wearer, examiner).ToString();
            var realName = entities.GetComponent<MetaDataComponent>(mantle).EntityName;
            Assert.That(text, Does.Contain(realName), "Real clothing must remain visible in the examination.");
            foreach (var slot in new[] { "eyes", "mask" })
            {
                Assert.That(inventory.TryGetSlotEntity(wearer, slot, out var blocker), Is.True);
                Assert.That(entities.GetComponent<VirtualItemComponent>(blocker!.Value).BlockingEntity, Is.EqualTo(mantle));
                var placeholder = entities.GetComponent<MetaDataComponent>(blocker.Value).EntityName;
                Assert.That(text, Does.Not.Contain(placeholder), $"The {slot} blocker must stay hidden.");
            }
            server.PlayerMan.SetAttachedEntity(pair.Player!, null);
            entities.DeleteEntity(map.MapUid);
        });
        await pair.RunTicksSync(20);
        await pair.CleanReturnAsync();
    }
}
