using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using ExtendedItemDataFramework;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Cooking;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("randyknapp.mods.extendeditemdataframework")]
public class Cooking : BaseUnityPlugin
{
	private const string ModName = "Cooking";
	private const string ModVersion = "1.1.1";
	private const string ModGUID = "org.bepinex.plugins.cooking";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<float> healthIncreaseFactor = null!;
	private static ConfigEntry<float> staminaIncreaseFactor = null!;
	private static ConfigEntry<float> regenIncreaseFactor = null!;
	private static ConfigEntry<int> happyMinimumLevel = null!;
	private static ConfigEntry<int> happyBuffDuration = null!;
	private static ConfigEntry<float> happyBuffStrengthFactor = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	public void Awake()
	{
		Skill cooking = new("Cooking", "cooking.png");
		cooking.Description.English("Increases health, stamina and health regen for food you cook.");
		cooking.Name.German("Kochen");
		cooking.Description.German("Erhöht die Lebenspunkte, Ausdauer und Lebenspunkteregeneration für von dir gekochtes Essen.");
		cooking.Configurable = false;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		healthIncreaseFactor = config("2 - Cooking", "Health Increase Factor", 2f, new ConfigDescription("Factor for the health on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		staminaIncreaseFactor = config("2 - Cooking", "Stamina Increase Factor", 2f, new ConfigDescription("Factor for the stamina on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		regenIncreaseFactor = config("2 - Cooking", "Regen Increase Factor", 2f, new ConfigDescription("Factor for the health regeneration on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		happyMinimumLevel = config("3 - Happy", "Happy Required Level", 50, new ConfigDescription("Minimum required cooking skill level for a chance to cook perfect food. 0 is disabled", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		happyBuffDuration = config("3 - Happy", "Happy Buff Duration", 3, new ConfigDescription("Duration for the happy buff from eating perfectly cooked food in minutes.", new AcceptableValueRange<int>(1, 60)));
		happyBuffStrengthFactor = config("3 - Happy", "Happy Buff Strength", 1.1f, new ConfigDescription("Factor for the movement speed with the happy buff active.", new AcceptableValueRange<float>(1f, 3f)));
		experienceGainedFactor = config("4 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the cooking skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) => cooking.SkillGainFactor = experienceGainedFactor.Value;
		cooking.SkillGainFactor = experienceGainedFactor.Value;

		float oldHealthIncreaseFactor = healthIncreaseFactor.Value;
		healthIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in SaveSkill.active)
			{
				ItemShared.Key.m_food *= (1 + ItemShared.Value * (healthIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldHealthIncreaseFactor - 1) / 100);
			}
			oldHealthIncreaseFactor = healthIncreaseFactor.Value;
		};

		float oldStaminaIncreaseFactor = staminaIncreaseFactor.Value;
		staminaIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in SaveSkill.active)
			{
				ItemShared.Key.m_foodStamina *= (1 + ItemShared.Value * (staminaIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldStaminaIncreaseFactor - 1) / 100);
			}
			oldStaminaIncreaseFactor = staminaIncreaseFactor.Value;
		};

		float oldRegenIncreaseFactor = regenIncreaseFactor.Value;
		regenIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in SaveSkill.active)
			{
				ItemShared.Key.m_foodRegen *= (1 + ItemShared.Value * (regenIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldRegenIncreaseFactor - 1) / 100);
			}
			oldRegenIncreaseFactor = regenIncreaseFactor.Value;
		};

		happyBuffDuration.SettingChanged += (_, _) => AddStatusEffect.SetValues();
		happyBuffStrengthFactor.SettingChanged += (_, _) => AddStatusEffect.SetValues();

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		ExtendedItemData.NewExtendedItemData += e =>
		{
			if (CheckCooking.cookingPlayer is not null && e.m_shared.m_food > 0 && e.m_shared.m_foodStamina > 0)
			{
				SaveSkill skill = e.AddComponent<SaveSkill>();
				skill.skill = Mathf.RoundToInt(CheckCooking.cookingPlayer.m_nview.GetZDO().GetFloat("Cooking Skill Factor") * 100 / 5) * 5;
				if (happyMinimumLevel.Value > 0 && Random.Range(0, 100) <= skill.skill - happyMinimumLevel.Value)
				{
					skill.happy = true;
				}
				skill.Apply();
				CheckCooking.cookingPlayer.m_nview.InvokeRPC("Cooking IncreaseSkill", 5);
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

		[UsedImplicitly]
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
		private static StatusEffect? happy;

		private static void Postfix(ObjectDB __instance)
		{
			happy = ScriptableObject.CreateInstance<IncreaseMovementSpeed>();
			happy.name = "Happy";
			happy.m_name = "Happy";
			happy.m_icon = loadSprite("happy.png", 64, 64);
			SetValues();
			__instance.m_StatusEffects.Add(happy);
		}

		public static void SetValues()
		{
			if (happy is not null)
			{
				happy.m_tooltip = $"You ate a perfect meal. Your movement speed is increased by {(happyBuffStrengthFactor.Value - 1) * 100}%.";
				happy.m_ttl = happyBuffDuration.Value * 60f;
			}
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
			speed *= happyBuffStrengthFactor.Value;
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
				float skill = Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor(Skill.fromName("Cooking")) * 100 / 5) * 5f / 100;
				if (skill > 0)
				{
					__result = new Regex("(\\$item_food_health.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_food * (healthIncreaseFactor.Value - 1))}</color>)");
					__result = new Regex("(\\$item_food_stamina.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodStamina * (staminaIncreaseFactor.Value - 1))}</color>)");
					__result = new Regex("(\\$item_food_regen.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodRegen * (regenIncreaseFactor.Value - 1))}</color>)");
				}
			}
			if (!crafting && item.Extended()?.GetComponent<SaveSkill>()?.happy == true)
			{
				__result += "\nThis food has been cooked perfectly and will make you happy.";
			}
		}

		[UsedImplicitly]
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			FieldInfo food = AccessTools.DeclaredField(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_food));
			FieldInfo foodStamina = AccessTools.DeclaredField(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_foodStamina));
			FieldInfo foodRegen = AccessTools.DeclaredField(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_foodRegen));

			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.opcode == OpCodes.Ldfld && (instruction.OperandIs(food) || instruction.OperandIs(foodStamina) || instruction.OperandIs(foodRegen)))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Mathf), nameof(Mathf.Round)));
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Save))]
	public class SaveFoodBonus
	{
		[UsedImplicitly]
		public static void Prefix(Player __instance)
		{
			foreach (Player.Food food in __instance.m_foods)
			{
				__instance.m_knownStations["Cooking Skill " + food.m_name] = food.m_item.Extended()?.GetComponent<SaveSkill>()?.skill ?? 0;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Load))]
	public class LoadFoodBonus
	{
		[UsedImplicitly]
		public static void Postfix(Player __instance)
		{
			foreach (Player.Food food in __instance.m_foods)
			{
				if (__instance.m_knownStations.TryGetValue("Cooking Skill " + food.m_name, out int skillLevel) && skillLevel > 0)
				{
					if (!food.m_item.IsExtended())
					{
						food.m_item = new ExtendedItemData(food.m_item);
					}
					food.m_item.m_shared = (ItemDrop.ItemData.SharedData)AccessTools.Method(typeof(ItemDrop.ItemData.SharedData), "MemberwiseClone").Invoke(food.m_item.m_shared, Array.Empty<object>());

					SaveSkill skill = food.m_item.Extended().AddComponent<SaveSkill>();
					skill.skill = skillLevel;
					skill.Apply();
				}
			}
		}
	}

	[PublicAPI]
	public class SaveSkill : BaseExtendedItemComponent, ExtendedItemUnique<SaveSkill>
	{
		public static readonly Dictionary<ItemDrop.ItemData.SharedData, int> active = new();
		private int _skill;

		public int skill
		{
			get => _skill;
			set
			{
				_skill = value;
				active[ItemData.m_shared] = value;
			}
		}

		public bool happy = false;

		public SaveSkill(ExtendedItemData parent) : base(typeof(SaveSkill).AssemblyQualifiedName, parent)
		{
			active[ItemData.m_shared] = 0;
		}

		public override string Serialize() => (happy ? "1" : "0") + skill;

		public override void Deserialize(string data)
		{
			if (data.Length > 0)
			{
				happy = data[0] == '1';
				int.TryParse(data.Substring(1), out int skillValue);
				skill = skillValue;
			}
		}

		public override BaseExtendedItemComponent Clone() => (BaseExtendedItemComponent)MemberwiseClone();

		public void Apply()
		{
			ItemData.m_shared.m_food *= 1 + skill * (healthIncreaseFactor.Value - 1) / 100;
			ItemData.m_shared.m_foodStamina *= 1 + skill * (staminaIncreaseFactor.Value - 1) / 100;
			ItemData.m_shared.m_foodRegen *= 1 + skill * (regenIncreaseFactor.Value - 1) / 100;
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
