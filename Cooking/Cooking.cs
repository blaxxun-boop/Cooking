using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using ExtendedItemDataFramework;
using HarmonyLib;
using JetBrains.Annotations;
using SkillManager;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Cooking;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("randyknapp.mods.extendeditemdataframework")]
public class Cooking : BaseUnityPlugin
{
	private const string ModName = "Cooking";
	private const string ModVersion = "1.0.0";
	private const string ModGUID = "org.bepinex.plugins.cooking";

	private static Skill cooking = null!;

	private static float oldCookingEffectFactor;

	public void Awake()
	{
		cooking = new("Cooking", "cooking.png");
		cooking.Description.English("Increases health, stamina and health regen for food you cook.");
		cooking.Name.German("Kochen");
		cooking.Description.German("Erhöht die Lebenspunkte, Ausdauer und Lebenspunkteregeneration für von dir gekochtes Essen.");
		cooking.Configurable = true;
		oldCookingEffectFactor = cooking.SkillEffectFactor;
		cooking.SkillEffectFactorChanged += factor =>
		{
			foreach (ItemDrop.ItemData.SharedData ItemShared in SaveSkill.active)
			{
				ItemShared.m_food *= factor / oldCookingEffectFactor;
				ItemShared.m_foodRegen *= factor / oldCookingEffectFactor;
				ItemShared.m_foodStamina *= factor / oldCookingEffectFactor;
			}
			oldCookingEffectFactor = factor;
		};

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		ExtendedItemData.NewExtendedItemData += e =>
		{
			if (CheckCooking.cookingPlayer is not null && e.m_shared.m_food > 0 && e.m_shared.m_foodStamina > 0)
			{
				SaveSkill skill = e.AddComponent<SaveSkill>();
				skill.skill = Mathf.RoundToInt(CheckCooking.cookingPlayer.m_nview.GetZDO().GetFloat("Cooking Skill Factor") * 100 / 5) * 5;
				if (Random.Range(0, 100) <= skill.skill - 50)
				{
					skill.happy = true;
				}
				skill.Apply();
				CheckCooking.cookingPlayer.m_nview.InvokeRPC("Cooking IncreaseSkill", 5f);
			}
		};
		ExtendedItemData.LoadExtendedItemData += e => e.GetComponent<SaveSkill>()?.Apply();
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	public class PlayerAwake
	{
		private static void Postfix(Player __instance)
		{
			__instance.m_nview.Register("Cooking IncreaseSkill", (long _, int factor) => __instance.RaiseSkill("Cooking", factor));
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Update))]
	public class PlayerUpdate
	{
		private static void Postfix(Player __instance)
		{
			if (__instance == Player.m_localPlayer)
			{
				__instance.m_nview.GetZDO().Set("Cooking Skill Factor", __instance.GetSkillFactor(Skill.fromName("Cooking")));
			}
		}
	}
	
	[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.RPC_RemoveDoneItem))]
	public class XPOnDoneItems
	{
		private static void Prefix(long sender)
		{
			if (Player.m_players.FirstOrDefault(p => p.m_nview.GetZDO().m_uid.m_userID == sender) is { } player)
			{
				CheckCooking.cookingPlayer = player;
			}
		}

		public static void Finalizer() => CheckCooking.cookingPlayer = null;
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	public class CheckCooking
	{
		public static Player? cookingPlayer = null;

		[UsedImplicitly]
		public static void Prefix() => cookingPlayer = Player.m_localPlayer;

		[UsedImplicitly]
		public static void Finalizer() => cookingPlayer = null;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
	public class ApplyStatusEffectToPlayer
	{
		private static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result)
		{
			if (item.Extended()?.GetComponent<SaveSkill>()?.happy == true && __result)
			{
				__instance.GetSEMan().AddStatusEffect("Happy");
			}
		}
	}

	[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
	public class AddStatusEffect
	{
		private static void Postfix(ObjectDB __instance)
		{
			StatusEffect happy = ScriptableObject.CreateInstance<IncreaseMovementSpeed>();
			happy.name = "Happy";
			happy.m_name = "Happy";
			happy.m_tooltip = "You ate a perfect meal. Your movement speed is increased by 10%.";
			happy.m_ttl = 180f;
			happy.m_icon = loadSprite("happy.png", 64, 64);
			__instance.m_StatusEffects.Add(happy);
		}
	}

	private static byte[] ReadEmbeddedFileBytes(string name)
	{
		using MemoryStream stream = new();
		Assembly.GetExecutingAssembly().GetManifestResourceStream("Cooking." + name)?.CopyTo(stream);
		return stream.ToArray();
	}

	private static Texture2D loadTexture(string name)
	{
		Texture2D texture = new(0, 0);
		texture.LoadImage(ReadEmbeddedFileBytes("icons." + name));
		return texture;
	}

	private static Sprite loadSprite(string name, int width, int height) => Sprite.Create(loadTexture(name), new Rect(0, 0, width, height), Vector2.zero);

	public class IncreaseMovementSpeed : StatusEffect
	{
		public override void ModifySpeed(float baseSpeed, ref float speed)
		{
			speed *= 1.1f;
		}
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool))]
	public class UpdateFoodDisplay
	{
		[UsedImplicitly]
		public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
		{
			if (crafting && item.m_shared.m_food > 0 && item.m_shared.m_foodStamina > 0)
			{
				float skill = Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor(Skill.fromName("Cooking")) * 100 / 5) * 5f / 100 * cooking.SkillEffectFactor;
				if (skill > 0)
				{
					__result = new Regex("(\\$item_food_health.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_food)}</color>)");
					__result = new Regex("(\\$item_food_stamina.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodStamina)}</color>)");
					__result = new Regex("(\\$item_food_regen.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodRegen)}</color>)");
				}
			}
			if (!crafting && item.Extended()?.GetComponent<SaveSkill>()?.happy == true)
			{
				__result += "\nThis food has been cooked perfectly and will make you happy.";
			}
		}
	}

	[PublicAPI]
	public class SaveSkill : BaseExtendedItemComponent, ExtendedItemUnique<SaveSkill>
	{
		public static readonly HashSet<ItemDrop.ItemData.SharedData> active = new();
		public int skill;
		public bool happy = false;

		public SaveSkill(ExtendedItemData parent) : base(typeof(SaveSkill).AssemblyQualifiedName, parent)
		{
			active.Add(ItemData.m_shared);
		}

		public override string Serialize() => (happy ? "1" : "0") + skill;

		public override void Deserialize(string data)
		{
			if (data.Length > 0)
			{
				happy = data[0] == '1';
				int.TryParse(data.Substring(1), out skill);
			}
		}

		public override BaseExtendedItemComponent Clone() => (BaseExtendedItemComponent)MemberwiseClone();

		public void Apply()
		{
			ItemData.m_shared.m_food *= 1 + skill * cooking.SkillEffectFactor / 100;
			ItemData.m_shared.m_foodStamina *= 1 + skill * cooking.SkillEffectFactor / 100;
			ItemData.m_shared.m_foodRegen *= 1 + skill * cooking.SkillEffectFactor / 100;
		}

		public bool Equals(SaveSkill other) => skill == other.skill && happy == other.happy;

		~SaveSkill()
		{
			active.Remove(ItemData.m_shared);
		}
	}

	[PublicAPI]
	public interface ExtendedItemUnique<in T> where T : BaseExtendedItemComponent
	{
		public bool Equals(T obj);
	}

	public static bool IsExtendedStackable(ItemDrop.ItemData? a, ItemDrop.ItemData? b)
	{
		if (a?.IsExtended() != true && b?.IsExtended() != true)
		{
			return true;
		}

		bool IsExtendedUniqueType(Type i) => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ExtendedItemUnique<>);

		if (b?.IsExtended() != true)
		{
			return a?.Extended().Components.Any(c => c.GetType().GetInterfaces().Any(IsExtendedUniqueType)) != true;
		}
		if (a.IsExtended() != true)
		{
			return b.Extended().Components.Any(c => c.GetType().GetInterfaces().Any(IsExtendedUniqueType)) != true;
		}

		Dictionary<Type, object> extendedUniques = a.Extended().Components.SelectMany(c =>
		{
			if (c.GetType().GetInterfaces().FirstOrDefault(IsExtendedUniqueType) is { } uniqueType)
			{
				return new[] { new KeyValuePair<Type, object>(uniqueType, c) };
			}

			return Enumerable.Empty<KeyValuePair<Type, object>>();
		}).ToDictionary(kv => kv.Key, kv => kv.Value);

		foreach (BaseExtendedItemComponent component in b.Extended().Components)
		{
			if (component.GetType().GetInterfaces().FirstOrDefault(IsExtendedUniqueType) is { } uniqueType)
			{
				if (extendedUniques.TryGetValue(uniqueType, out object other))
				{
					if (!(bool)uniqueType.GetMethod("Equals")!.Invoke(component, new[] { other }))
					{
						return false;
					}

					extendedUniques.Remove(uniqueType);
				}
				else
				{
					return false;
				}
			}
		}

		// All Unique components present in a were also present in b
		return extendedUniques.Count == 0;
	}

	[HarmonyPatch]
	public class CheckExtendedUniqueCanAddItem
	{
		private static IEnumerable<MethodBase> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.CanAddItem), new[] { typeof(ItemDrop.ItemData), typeof(int) }),
			AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) }),
		};

		private static void Prefix(ItemDrop.ItemData item) => CheckExtendedUniqueFindFreeStack.CheckingFor = item;
		private static void Finalizer() => CheckExtendedUniqueFindFreeStack.CheckingFor = null;
	}

	[HarmonyPatch]
	public class CheckExtendedUniqueFindFreeStack
	{
		public static ItemDrop.ItemData? CheckingFor;

		private static IEnumerable<MethodBase> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.FindFreeStackSpace)),
			AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.FindFreeStackItem))
		};

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructionsEnumerable)
		{
			CodeInstruction[] instructions = instructionsEnumerable.ToArray();
			Label target = (Label)instructions.First(i => i.opcode == OpCodes.Br || i.opcode == OpCodes.Br_S).operand;
			CodeInstruction targetedInstr = instructions.First(i => i.labels.Contains(target));
			CodeInstruction lastBranch = instructions.Reverse().First(i => i.Branches(out Label? label) && targetedInstr.labels.Contains(label!.Value));
			CodeInstruction? loadingInstruction = null;

			for (int i = 0; i < instructions.Length; ++i)
			{
				yield return instructions[i];
				// get hold of the loop variable store (the itemdata we want to compare against)
				if (loadingInstruction == null && instructions[i].opcode == OpCodes.Call && ((MethodInfo)instructions[i].operand).Name == "get_Current")
				{
					loadingInstruction = instructions[i + 1].Clone();
					loadingInstruction.opcode = new Dictionary<OpCode, OpCode>
					{
						{ OpCodes.Stloc_0, OpCodes.Ldloc_0 },
						{ OpCodes.Stloc_1, OpCodes.Ldloc_1 },
						{ OpCodes.Stloc_2, OpCodes.Ldloc_2 },
						{ OpCodes.Stloc_3, OpCodes.Ldloc_3 },
						{ OpCodes.Stloc_S, OpCodes.Ldloc_S }
					}[loadingInstruction.opcode];
				}
				if (instructions[i] == lastBranch)
				{
					yield return loadingInstruction!;
					yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.DeclaredField(typeof(CheckExtendedUniqueFindFreeStack), nameof(CheckingFor)));
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Cooking), nameof(IsExtendedStackable)));
					yield return new CodeInstruction(OpCodes.Brfalse, target);
				}
			}
		}
	}

	[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int))]
	public class CheckExtendedUniqueAddItem
	{
		private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, int x, int y, ref bool __result)
		{
			if (__instance.GetItemAt(x, y) is { } itemAt && !Cooking.IsExtendedStackable(itemAt, item))
			{
				__result = false;
				return false;
			}

			return true;
		}
	}
}
