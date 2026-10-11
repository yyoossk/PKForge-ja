// 起動テスト用: ポケモン入りの日本語版セーブを作る。
// PKHeX の「空のセーブ」は世代によっては書き出せないことがあるので、書き出せて読み直せる世代を順に試す。
//   dotnet run --project app/savegen -p:PKHeXCore=<PKHeX.Core.csproj> -- out.sav
using PKHeX.Core;

var output = args.Length > 0 ? args[0] : "PKForgeTest.sav";
var lang = (int)LanguageID.Japanese;
ushort[] favorites = [25, 133, 6, 9, 3, 143, 149, 151, 150, 94, 130, 131, 248, 249, 250, 196, 197, 175,
                      252, 255, 258, 282, 334, 384, 382, 383, 380, 381, 385, 386, 387, 390, 393, 448, 445, 493];
GameVersion[] candidates = [GameVersion.RD, GameVersion.C, GameVersion.E, GameVersion.FR, GameVersion.Pt, GameVersion.B2, GameVersion.US];

foreach (var version in candidates)
{
    try
    {
        var sav = BlankSaveFile.Get(version, "テスト", LanguageID.Japanese);
        var species = favorites.Where(s => s <= sav.MaxSpeciesID).ToArray();

        PKM Make(ushort sp, byte level, int seed)
        {
            var pk = sav.BlankPKM;
            pk.Species = sp;
            pk.CurrentLevel = level;
            pk.Language = lang;
            pk.Nickname = SpeciesName.GetSpeciesNameGeneration(sp, lang, sav.Generation);
            pk.IsNicknamed = false;
            pk.OriginalTrainerName = sav.OT;
            pk.ID32 = sav.ID32;
            pk.Version = version;
            pk.Ball = 4;
            pk.MetLevel = level;
            pk.Move1 = 33;
            pk.Move2 = 45;
            pk.PID = (uint)(0x12345678 + seed * 7919);
            pk.IV_HP = 15; pk.IV_ATK = 10 + seed % 6; pk.IV_DEF = 12; pk.IV_SPA = 15; pk.IV_SPD = 15; pk.IV_SPE = 14;
            pk.ResetPartyStats();
            pk.HealPP();
            pk.RefreshChecksum();
            return pk;
        }

        var perBox = Math.Min(sav.BoxSlotCount, 30);
        for (var i = 0; i < perBox; i++)
            sav.SetBoxSlotAtIndex(Make(species[i % species.Length], (byte)(5 + i * 2), i), 0, i);
        for (var i = 0; i < Math.Min(12, sav.BoxSlotCount); i++)
            sav.SetBoxSlotAtIndex(Make(species[(i * 7) % species.Length], (byte)(40 + i), 100 + i), 1, i);
        for (var i = 0; i < 6; i++)
            sav.SetPartySlotAtIndex(Make(species[(i * 5) % species.Length], (byte)(30 + i * 5), 200 + i), i);

        var data = sav.Write().ToArray();
        var check = SaveUtil.GetSaveFile(data);
        if (check is null)
        {
            Console.WriteLine($"{version}: 書き出したが読み直せない");
            continue;
        }
        File.WriteAllBytes(output, data);
        Console.WriteLine($"{output}: {version} / {check.GetType().Name} / party {check.PartyCount} / {data.Length} bytes");
        return 0;
    }
    catch (Exception e)
    {
        Console.WriteLine($"{version}: {e.GetType().Name}: {e.Message}");
    }
}
Console.WriteLine("どの世代でも作れませんでした");
return 1;
