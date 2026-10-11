// 起動テスト用: ポケモン入りの日本語版プラチナのセーブを作る
//   dotnet run --project app/savegen -p:PKHeXCore=<PKHeX.Core.csproj> -- out.sav
using PKHeX.Core;

var output = args.Length > 0 ? args[0] : "PKForgeTest.sav";
var sav = BlankSaveFile.Get(GameVersion.Pt, "ヒカリ", LanguageID.Japanese);
ushort[] species = [387, 390, 393, 25, 133, 448, 445, 151, 150, 6, 9, 3, 143, 149, 248, 384, 483, 484, 487, 491, 492, 493, 175, 196, 197, 470, 471, 206, 94, 130];
var lang = (int)LanguageID.Japanese;

PKM Make(ushort sp, byte level, int seed)
{
    var pk = sav.BlankPKM;
    pk.Species = sp;
    pk.CurrentLevel = level;
    pk.Language = lang;
    pk.Nickname = SpeciesName.GetSpeciesNameGeneration(sp, lang, 4);
    pk.IsNicknamed = false;
    pk.OriginalTrainerName = sav.OT;
    pk.ID32 = sav.ID32;
    pk.Version = GameVersion.Pt;
    pk.Ball = 4;
    pk.MetLevel = level;
    pk.MetLocation = 16;
    pk.Move1 = 33;
    pk.Move2 = 45;
    pk.PID = (uint)(0x12345678 + seed * 7919);
    pk.IV_HP = 31; pk.IV_ATK = 20 + seed % 12; pk.IV_DEF = 25; pk.IV_SPA = 31; pk.IV_SPD = 18; pk.IV_SPE = 31;
    pk.ResetPartyStats();
    pk.HealPP();
    pk.RefreshChecksum();
    return pk;
}

for (var i = 0; i < 30; i++)
    sav.SetBoxSlotAtIndex(Make(species[i], (byte)(5 + i * 3), i), 0, i);
for (var i = 0; i < 12; i++)
    sav.SetBoxSlotAtIndex(Make(species[(i * 7) % species.Length], (byte)(50 + i), 100 + i), 1, i);
for (var i = 0; i < 6; i++)
    sav.SetPartySlotAtIndex(Make(species[i * 4 % species.Length], (byte)(30 + i * 5), 200 + i), i);

File.WriteAllBytes(output, sav.Write().ToArray());
Console.WriteLine($"{output}: {sav.Version} {sav.OT} party {sav.PartyCount}");
