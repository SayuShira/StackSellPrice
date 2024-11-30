using System;
using System.Text.RegularExpressions;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

namespace StackSellPrice;

public class Plugin: IDalamudPlugin {
	private const bool Debug = true;
	private const string QuantityPattern = @"(\d+)\/"; // Number followed by a /
	private const string PricePattern = @"(\d{1,3}(?:[.,]\d{3})*)"; // Numbers with separators: 13.000/13,000
	private bool disposed;

	[PluginService] private static IGameGui gameGui { get; set; } = null!;
	[PluginService] private static IDataManager gameData { get; set; } = null!;
	[PluginService] private static IPluginLog log { get; set; } = null!;
	[PluginService] private static IAddonLifecycle addonLifecycle { get; set; } = null!;

	public Plugin() {
		addonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail",
			(_, args) => this.addSellStackPrice(args));
		log.Information("Registered ItemDetail AddonListener!");
	}

	private unsafe void addSellStackPrice(AddonArgs args) {
		if (args is not AddonRequestedUpdateArgs requestedUpdateArgs) return;
		NumberArrayData* numberArrayData = ((NumberArrayData**)requestedUpdateArgs.NumberArrayData)[29];
		// No idea what this test does - Sourced from WhichPatchWasThatPlugin.cs along the general tooltip modify idea
		// Blocks one execution of the function so far.
		if ((numberArrayData->IntArray[3] & 1) == 0) return;

		StringArrayData* stringArrayData = ((StringArrayData**)requestedUpdateArgs.StringArrayData)[26];

		// Text: [Patch 6.4]   46/999 (Total: 0 /  46) with PatchInfo active => Look for first "number/"
		SeString quantitySeStr = getTooltipString(stringArrayData, (int)ItemTooltipString.Quantity);
		Match quantityMatch = Regex.Match(quantitySeStr.ToString(), QuantityPattern);
		if (!uint.TryParse(quantityMatch.Groups[1].Value, out uint quantity)) return;
		if (Debug) log.Debug("Stack Size: " + quantity);

		// Text: Sells for 33 gil or Unsellable (\u3000Market Prohibited) => Look for first numbers
		SeString priceSeStr = getTooltipString(stringArrayData, (int)ItemTooltipString.VendorSellPrice);
		Match priceMatch = Regex.Match(priceSeStr.ToString(), PricePattern);
		if (!uint.TryParse(priceMatch.Groups[1].Value.Replace(",", "").Replace(".", ""), out uint price)) return;
		if (Debug) log.Debug("Sells for: " + price);
		
		// if editTooltip changes the tooltip it will return true
		if (!this.editTooltip(priceSeStr, quantity, price)) return;
		stringArrayData->SetValue((int)ItemTooltipString.VendorSellPrice, priceSeStr.Encode(), false, true, true);
		log.Debug("Should be changed to: " + priceSeStr.TextValue);
	}

	private static unsafe SeString getTooltipString(StringArrayData* stringArrayData, int field) {
		IntPtr stringAddress = new nint(stringArrayData->StringArray[field]);
		return stringAddress != nint.Zero ? MemoryHelper.ReadSeStringNullTerminated(stringAddress) : new SeString();
	}

	private bool editTooltip(SeString priceSeStr, uint quantity, uint price) {
		if (priceSeStr.TextValue.Contains(SeIconChar.Gil.ToIconString()))
			return false;

		if (price <= 0) {
			log.Warning($"Price <{price}> out of range");
			return false;
		}

		ulong itemId = gameGui.HoveredItem;
		double luminaPrice = hqAdjustedLuminaPrice(itemId); // Could just use lumina for price, currently checking for differences

		if (Math.Abs(price - luminaPrice) > 0)
			log.Debug($"Price difference detected: Lumina ({luminaPrice}, Parsed: {price})");

		bool isMarketProhibited = priceSeStr.ToString().Contains("\u3000Market Prohibited");
		if (Debug) log.Verbose("Text: " + priceSeStr);

		string gilIcon = SeIconChar.Gil.ToIconString();

		// Insert gil icon and remove extra strings from the start of this node
		priceSeStr.Payloads[0] = new TextPayload(priceSeStr.ToString()
			.Replace(" gil", $"{gilIcon}")
			.Replace("\u3000Market Prohibited", ""));

		if (quantity <= 1) {
			priceSeStr.Payloads.Insert(0, new UIForegroundPayload(529));
			priceSeStr.Payloads.Add(new UIForegroundPayload(0));
		}
		else {
			priceSeStr.Payloads.Add(new UIForegroundPayload(3));
			priceSeStr.Payloads.Add(new TextPayload($"(x{quantity:N0} = "));
			priceSeStr.Payloads.Add(new UIForegroundPayload(0));
			priceSeStr.Payloads.Add(new UIForegroundPayload(529));
			priceSeStr.Payloads.Add(new TextPayload($"{price * quantity:N0}{gilIcon}"));
			priceSeStr.Payloads.Add(new UIForegroundPayload(0));
			priceSeStr.Payloads.Add(new UIForegroundPayload(3));
			priceSeStr.Payloads.Add(new TextPayload(") ")); // Extra space is present in vanilla before \u3000, too
			priceSeStr.Payloads.Add(new UIForegroundPayload(0));
		}

		if (isMarketProhibited) priceSeStr.Payloads.Add(new TextPayload("\u3000Market Prohibited"));
		return true;
	}

	private static double hqAdjustedLuminaPrice(ulong itemId) {
		bool hq = false;
		// ReSharper disable once ConvertIfStatementToSwitchStatement
		if (itemId is >= 1_000_000 and < 1_500_000) {
			itemId -= 1_000_000;
			hq = true;
		}
		// hack for materia prices being handled as if they're HQ even though they can't actually /be/ HQ, thanks SE, what the fuck
		else if (itemId is (>= 5604 and <= 5723) // Strength Materia I to Quicktongue Materia V
		         or (>= 18006 and <= 18029) // Strength Materia VI to Quicktongue Materia VI
		         or (>= 25186 and <= 25198) // Piety Materia VII to Quicktongue Materia VII
		         or (>= 26727 and <= 26739) // Piety Materia VIII to Quicktongue Materia VIII
		         or (>= 33917 and <= 33942) // Piety Materia IX to Quicktongue Materia X
		         or (>= 41757 and <= 41782)) // Piety Materia XI to Quicktongue Materia XII
			hq = true;

		double luminaPrice = gameData.GetExcelSheet<Item>().GetRow((uint)itemId).PriceLow;
		if (hq) luminaPrice += Math.Ceiling(luminaPrice / 10);

		return luminaPrice;
	}

	#region IDisposable

	protected virtual void Dispose(bool disposing) {
		if (this.disposed)
			return;
		this.disposed = true;

		if (disposing) {
			addonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail");
			log.Information("Unregistered ItemDetail AddonListener!");
		}

		log.Information("Goodbye friend :)");
	}

	public void Dispose() {
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}

	#endregion
}
