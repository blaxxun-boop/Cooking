using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ItemDataManager;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Cooking;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class Cooking : BaseUnityPlugin
{
	private const string ModName = "Cooking";
	private const string ModVersion = "1.2.2";
	private const string ModGUID = "org.bepinex.plugins.cooking";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<float> healthIncreaseFactor = null!;
	private static ConfigEntry<float> staminaIncreaseFactor = null!;
	private static ConfigEntry<float> regenIncreaseFactor = null!;
	private static ConfigEntry<float> eitrIncreaseFactor = null!;
	private static ConfigEntry<int> happyMinimumLevel = null!;
	private static ConfigEntry<int> happyBuffDuration = null!;
	private static ConfigEntry<float> happyBuffStrengthFactor = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;
	private static ConfigEntry<int> experienceLoss = null!;
	private static ConfigEntry<int> bonusCraftingChance = null!;
	private static ConfigEntry<int> bonusCraftingAmount = null!;
	private static ConfigEntry<int> craftingTimeReduction = null!;
	private static ConfigEntry<int> cookingSkillInterval = null!;

	private static Skills.SkillDef? cookingSkill;

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
		Off = 0,
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		healthIncreaseFactor = config("2 - Cooking", "Health Increase Factor", 1.3f, new ConfigDescription("Factor for the health on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		staminaIncreaseFactor = config("2 - Cooking", "Stamina Increase Factor", 1.3f, new ConfigDescription("Factor for the stamina on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		regenIncreaseFactor = config("2 - Cooking", "Regen Increase Factor", 1.3f, new ConfigDescription("Factor for the health regeneration on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		eitrIncreaseFactor = config("2 - Cooking", "Eitr Increase Factor", 1.3f, new ConfigDescription("Factor for the eitr on food items at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		bonusCraftingChance = config("2 - Cooking", "Bonus Crafting Chance", 0, new ConfigDescription("Chance to craft additional food items at skill level 100. Vanilla uses 25%.", new AcceptableValueRange<int>(0, 100)));
		bonusCraftingChance.SettingChanged += (_, _) =>
		{
			if (InventoryGui.instance is { } gui)
			{
				gui.m_craftBonusChance = bonusCraftingChance.Value / 100f;
			}
		};
		bonusCraftingAmount = config("2 - Cooking", "Bonus Crafting Amount", 1, new ConfigDescription("Additional items to be crafted when the bonus crafting chance triggers.", new AcceptableValueRange<int>(1, 10)));
		bonusCraftingAmount.SettingChanged += (_, _) =>
		{
			if (InventoryGui.instance is { } gui)
			{
				gui.m_craftBonusAmount = bonusCraftingAmount.Value;
			}
		};
		craftingTimeReduction = config("2 - Cooking", "Crafting Time Reduction", 30, new ConfigDescription("Time reduction to craft food items at skill level 100. Vanilla uses 60%.", new AcceptableValueRange<int>(0, 100)));
		cookingSkillInterval = config("2 - Cooking", "Cooking Skill Interval", 5, new ConfigDescription("Cooking skill level difference needed before food stops stacking with each other.", new AcceptableValueRange<int>(1, 50)));
		happyMinimumLevel = config("3 - Happy", "Happy Required Level", 50, new ConfigDescription("Minimum required cooking skill level for a chance to cook perfect food. 0 is disabled", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		happyBuffDuration = config("3 - Happy", "Happy Buff Duration", 3, new ConfigDescription("Duration for the happy buff from eating perfectly cooked food in minutes.", new AcceptableValueRange<int>(1, 60)));
		happyBuffStrengthFactor = config("3 - Happy", "Happy Buff Strength", 1.1f, new ConfigDescription("Factor for the movement speed with the happy buff active.", new AcceptableValueRange<float>(1f, 3f)));
		experienceGainedFactor = config("4 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the cooking skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) =>
		{
			if (cookingSkill is not null)
			{
				cookingSkill.m_increseStep = experienceGainedFactor.Value;
			}
		};
		experienceLoss = config("4 - Other", "Skill Experience Loss", 0, new ConfigDescription("How much experience to lose in the cooking skill on death.", new AcceptableValueRange<int>(0, 100)));

		float oldHealthIncreaseFactor = healthIncreaseFactor.Value;
		healthIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in CookingSkill.active)
			{
				ItemShared.Key.m_food *= (1 + ItemShared.Value * (healthIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldHealthIncreaseFactor - 1) / 100);
			}
			oldHealthIncreaseFactor = healthIncreaseFactor.Value;
		};

		float oldStaminaIncreaseFactor = staminaIncreaseFactor.Value;
		staminaIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in CookingSkill.active)
			{
				ItemShared.Key.m_foodStamina *= (1 + ItemShared.Value * (staminaIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldStaminaIncreaseFactor - 1) / 100);
			}
			oldStaminaIncreaseFactor = staminaIncreaseFactor.Value;
		};

		float oldRegenIncreaseFactor = regenIncreaseFactor.Value;
		regenIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in CookingSkill.active)
			{
				ItemShared.Key.m_foodRegen *= (1 + ItemShared.Value * (regenIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldRegenIncreaseFactor - 1) / 100);
			}
			oldRegenIncreaseFactor = regenIncreaseFactor.Value;
		};

		float oldEitrIncreaseFactor = eitrIncreaseFactor.Value;
		eitrIncreaseFactor.SettingChanged += (_, _) =>
		{
			foreach (KeyValuePair<ItemDrop.ItemData.SharedData, int> ItemShared in CookingSkill.active)
			{
				ItemShared.Key.m_foodEitr *= (1 + ItemShared.Value * (eitrIncreaseFactor.Value - 1) / 100) / (1 + ItemShared.Value * (oldEitrIncreaseFactor - 1) / 100);
			}
			oldEitrIncreaseFactor = eitrIncreaseFactor.Value;
		};

		happyBuffDuration.SettingChanged += (_, _) => AddStatusEffect.SetValues();
		happyBuffStrengthFactor.SettingChanged += (_, _) => AddStatusEffect.SetValues();

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		ItemInfo.ForceLoadTypes.Add(typeof(CookingSkill));
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.Awake))]
	private static class ReplaceSkillIcon
	{
		private static void Postfix(Skills __instance)
		{
			foreach (Skills.SkillDef skill in __instance.m_skills)
			{
				if (skill.m_skill == Skills.SkillType.Cooking)
				{
					cookingSkill = skill;
					skill.m_icon = loadSprite("cooking.png", 64, 64);
					skill.m_increseStep = experienceGainedFactor.Value;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	public class PlayerAwake
	{
		private static void Postfix(Player __instance)
		{
			__instance.m_nview.Register("Cooking IncreaseSkill", (long _, int factor) => __instance.RaiseSkill(Skills.SkillType.Cooking, factor));
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Update))]
	public class PlayerUpdate
	{
		private static void Postfix(Player __instance)
		{
			if (__instance == Player.m_localPlayer)
			{
				__instance.m_nview.GetZDO().Set("Cooking Skill Factor", __instance.GetSkillFactor(Skills.SkillType.Cooking));
			}
		}
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
	private static class SetCraftBonus
	{
		private static void Prefix(InventoryGui __instance)
		{
			__instance.m_craftBonusAmount = bonusCraftingAmount.Value;
			__instance.m_craftBonusChance = bonusCraftingChance.Value / 100f;
		}
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
	private static class ModifyCraftDuration
	{
		private static void Prefix(InventoryGui __instance, Player player, out float __state)
		{
			__state = __instance.m_craftDurationSkillMaxDecrease;
			if (player.GetCurrentCraftingStation()?.m_craftingSkill == Skills.SkillType.Cooking)
			{
				__instance.m_craftDurationSkillMaxDecrease = craftingTimeReduction.Value / 100f;
			}
		}

		private static void Finalizer(InventoryGui __instance, float __state)
		{
			__instance.m_craftDurationSkillMaxDecrease = __state;
		}
	}

	[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.RPC_RemoveDoneItem))]
	public class OnCookingDoneItems
	{
		public static Player? cookingPlayer;

		private static void Prefix(long sender)
		{
			if (Player.s_players.FirstOrDefault(p => p.m_nview.GetZDO().m_uid.UserID == sender) is { } player)
			{
				cookingPlayer = player;
			}
		}

		[UsedImplicitly]
		public static void Finalizer() => cookingPlayer = null;
	}

	private static void AttachCooking(ItemDrop item, Player cook)
	{
		if (item.m_itemData.m_shared.m_food > 0 && item.m_itemData.m_shared.m_foodStamina > 0)
		{
			CookingSkill skill = item.m_itemData.Data().Add<CookingSkill>()!;
			skill.skill = Mathf.RoundToInt(cook.m_nview.GetZDO().GetFloat("Cooking Skill Factor") * 100 / cookingSkillInterval.Value) * cookingSkillInterval.Value;
			if (happyMinimumLevel.Value > 0 && Random.Range(0, 100) <= skill.skill - happyMinimumLevel.Value)
			{
				skill.happy = true;
			}

			skill.Save();
			skill.Load();
		}
	}

	[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.SpawnItem))]
	public class XPOnDoneItems
	{
		private static void Prefix(string name, out ItemDrop __state)
		{
			__state = ObjectDB.instance.GetItemPrefab(name).GetComponent<ItemDrop>();
			// ReSharper disable once Unity.NoNullCoalescing
			AttachCooking(__state, OnCookingDoneItems.cookingPlayer ?? Player.m_localPlayer);
			OnCookingDoneItems.cookingPlayer?.m_nview.InvokeRPC("Cooking IncreaseSkill", 5);
		}

		private static void Finalizer(ItemDrop __state)
		{
			__state.m_itemData.Data().Remove<CookingSkill>();
		}
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	public class CheckCooking
	{
		[UsedImplicitly]
		public static void Prefix(InventoryGui __instance)
		{
			if (__instance.m_craftRecipe is not null)
			{
				AttachCooking(__instance.m_craftRecipe.m_item, Player.m_localPlayer);
			}
		}

		[UsedImplicitly]
		public static void Finalizer(InventoryGui __instance)
		{
			__instance.m_craftRecipe?.m_item.m_itemData.Data().Remove<CookingSkill>();
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
	public class ApplyStatusEffectToPlayer
	{
		private static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result)
		{
			if (item.Data().Get<CookingSkill>()?.happy == true && __result)
			{
				__instance.GetSEMan().AddStatusEffect("Happy".GetStableHashCode());
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
		public override void ModifySpeed(float baseSpeed, ref float speed, Character character, Vector3 dir)
		{
			speed *= happyBuffStrengthFactor.Value;
		}
	}

	[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
	public class UpdateFoodDisplay
	{
		[UsedImplicitly]
		public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
		{
			if (crafting && item.m_shared.m_food > 0 && item.m_shared.m_foodStamina > 0)
			{
				float skill = Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Cooking) * 100 / 5) * 5f / 100;
				if (skill > 0)
				{
					__result = new Regex("(\\$item_food_health.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_food * (healthIncreaseFactor.Value - 1))}</color>)");
					__result = new Regex("(\\$item_food_stamina.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodStamina * (staminaIncreaseFactor.Value - 1))}</color>)");
					__result = new Regex("(\\$item_food_regen.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodRegen * (regenIncreaseFactor.Value - 1))}</color>)");
					if (item.m_shared.m_foodEitr > 0)
					{
						__result = new Regex("(\\$item_food_eitr.*?</color>)").Replace(__result, $"$1 (<color=orange>+{Mathf.Round(skill * item.m_shared.m_foodEitr * (eitrIncreaseFactor.Value - 1))}</color>)");
					}
				}
			}
			if (!crafting && item.Data().Get<CookingSkill>()?.happy == true)
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
				__instance.m_knownStations["Cooking Skill " + food.m_name] = food.m_item.Data().Get<CookingSkill>()?.skill ?? 0;
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
					food.m_item.m_shared = (ItemDrop.ItemData.SharedData)AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone").Invoke(food.m_item.m_shared, Array.Empty<object>());
					CookingSkill? cooking = food.m_item.Data().Add<CookingSkill>();
					if (cooking is null)
					{
						Debug.LogError("COOKINGSKILL IS NULL");
						return;
					}
					cooking.Value = "0" + skillLevel;
					cooking.Load();
				}
			}
		}
	}

	[HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
	public class ChangeSkillLoss
	{
		private static void Prefix(Skills __instance, ref Skills.Skill? __state)
		{
			if (__instance.m_skillData.TryGetValue(Skills.SkillType.Cooking, out Skills.Skill skill))
			{
				__state = skill;
				if (experienceLoss.Value > 0)
				{
					skill.m_level -= skill.m_level * experienceLoss.Value / 100f;
					skill.m_accumulator = 0.0f;
				}
				__instance.m_skillData.Remove(Skills.SkillType.Cooking);
			}
		}

		private static void Finalizer(Skills __instance, ref Skills.Skill? __state)
		{
			if (__state is not null)
			{
				__instance.m_skillData[Skills.SkillType.Cooking] = __state;
			}
		}
	}

	private class CookingSkill : ItemData
	{
		public static readonly Dictionary<ItemDrop.ItemData.SharedData, int> active = new();

		public bool happy = false;
		public int skill = 0;

		~CookingSkill() => active.Remove(Item.m_shared);

		public override void Save()
		{
			Value = (happy ? "1" : "0") + skill;
		}

		public override void Load()
		{
			string data = Value;
			active[Item.m_shared] = skill;

			if (data.Length > 0)
			{
				happy = data[0] == '1';
				int.TryParse(data.Substring(1), out int skillValue);
				skill = skillValue;
			}

			if (!IsCloned)
			{
				Item.m_shared.m_food *= 1 + skill * (healthIncreaseFactor.Value - 1) / 100;
				Item.m_shared.m_foodStamina *= 1 + skill * (staminaIncreaseFactor.Value - 1) / 100;
				Item.m_shared.m_foodRegen *= 1 + skill * (regenIncreaseFactor.Value - 1) / 100;
				if (Item.m_shared.m_foodEitr > 0)
				{
					Item.m_shared.m_foodEitr *= 1 + skill * (eitrIncreaseFactor.Value - 1) / 100;
				}
			}
		}

		public override void Unload()
		{
			Item.m_shared = (ItemDrop.ItemData.SharedData)AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone").Invoke(Item.m_shared, Array.Empty<object>());
			Item.m_shared.m_food /= 1 + skill * (healthIncreaseFactor.Value - 1) / 100;
			Item.m_shared.m_foodStamina /= 1 + skill * (staminaIncreaseFactor.Value - 1) / 100;
			Item.m_shared.m_foodRegen /= 1 + skill * (regenIncreaseFactor.Value - 1) / 100;
			if (Item.m_shared.m_foodEitr > 0)
			{
				Item.m_shared.m_foodEitr /= 1 + skill * (eitrIncreaseFactor.Value - 1) / 100;
			}
		}

		protected override bool AllowStackingIdenticalValues { get; set; } = true;
	}

	[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.OnInteract))]
	private static class SkipCookingStationRaiseSkill
	{
		private static readonly MethodInfo raiseSkill = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.RaiseSkill));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(raiseSkill))
				{
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Pop);
					yield return new CodeInstruction(OpCodes.Pop);
				}
				else
				{
					yield return instruction;
				}
			}
		}
	}

	[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
	private static class IncreaseCraftingSkill
	{
		private static float ModifyBaseCraftIncrease(float skill) => skill * (InventoryGui.instance.m_craftRecipe.m_craftingStation.m_craftingSkill == Skills.SkillType.Cooking ? 5 : 1);

		private static readonly MethodInfo raiseSkill = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.RaiseSkill));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(raiseSkill))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(IncreaseCraftingSkill), nameof(ModifyBaseCraftIncrease)));
				}
				yield return instruction;
			}
		}
	}
}
