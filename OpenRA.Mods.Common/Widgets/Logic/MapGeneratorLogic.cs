#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileSystem;
using OpenRA.Mods.Common.MapGenerator;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class MapGeneratorLogic : ChromeLogic
	{
		[FluentReference]
		const string Tileset = "label-mapchooser-random-map-tileset";

		[FluentReference]
		const string MapSize = "label-mapchooser-random-map-size";

		[FluentReference]
		const string PreviewVisibility = "label-mapchooser-random-map-preview-visibility";

		[FluentReference]
		const string RandomMap = "label-mapchooser-random-map-title";

		[FluentReference]
		const string Generating = "label-mapchooser-random-map-generating";

		[FluentReference]
		const string GenerationFailed = "label-mapchooser-random-map-error";

		[FluentReference("players")]
		const string Players = "label-player-count";

		[FluentReference("author")]
		const string CreatedBy = "label-created-by";

		[FluentReference]
		const string MapSizeSmall = "label-map-size-small";

		[FluentReference]
		const string MapSizeMedium = "label-map-size-medium";

		[FluentReference]
		const string MapSizeLarge = "label-map-size-large";

		[FluentReference]
		const string MapSizeHuge = "label-map-size-huge";

		[FluentReference]
		const string PreviewVisibilityAll = "label-mapchooser-random-map-preview-visibility-all";

		[FluentReference]
		const string PreviewVisibilityMapChooser = "label-mapchooser-random-map-preview-visibility-mapchooser";

		[FluentReference]
		const string PreviewVisibilityNone = "label-mapchooser-random-map-preview-visibility-none";

		[FluentReference]
		const string BlueprintCopy = "button-mapchooser-blueprint-copy";

		[FluentReference]
		const string BlueprintCopied = "button-mapchooser-blueprint-copied";

		[FluentReference]
		const string RenameTitle = "dialog-rename-map.title";

		[FluentReference]
		const string RenamePrompt = "dialog-rename-map.prompt";

		[FluentReference]
		const string RenameAccept = "dialog-rename-map.confirm";

		public static readonly IReadOnlyDictionary<string, int2> MapSizes = new Dictionary<string, int2>()
		{
			{ MapSizeSmall, new int2(48, 60) },
			{ MapSizeMedium, new int2(60, 90) },
			{ MapSizeLarge, new int2(90, 120) },
			{ MapSizeHuge, new int2(120, 160) },
		};

		public static readonly IReadOnlyDictionary<string, MapGenerationArgs.PreviewVisibilityFlags> PreviewVisibilities =
			new Dictionary<string, MapGenerationArgs.PreviewVisibilityFlags>()
		{
			{ PreviewVisibilityAll, MapGenerationArgs.PreviewVisibilityFlags.All },
			{ PreviewVisibilityMapChooser, MapGenerationArgs.PreviewVisibilityFlags.MapChooser },
			{ PreviewVisibilityNone, MapGenerationArgs.PreviewVisibilityFlags.None },
		};

		readonly ModData modData;
		readonly IReadOnlyList<IEditorMapGeneratorInfo> generators;
		readonly List<ITerrainInfo> validTerrainInfos;
		readonly Action<MapGenerationArgs, IReadWritePackage> onGenerate;

		readonly GeneratedMapPreviewWidget preview;
		readonly ScrollPanelWidget optionsPanel;
		readonly Widget checkboxOptionTemplate;
		readonly Widget textOptionTemplate;
		readonly Widget dropdownOptionTemplate;
		readonly Widget tilesetOption;
		readonly Widget sizeOption;
		readonly Widget previewVisibilityOption;
		readonly Widget parentWidget;

		// Cameo: every generator's tilesets are offered in one list; picking a tileset switches to
		// the generator that owns it, so the active generator and its args are not fixed.
		IEditorMapGeneratorInfo generator;
		MapGenerationArgs generationArgs;

		ITerrainInfo selectedTerrain;
		string selectedSize;
		string selectedPreviewVisibility;
		bool initialGenerationDone;

		// The last successfully generated map, kept so the Rename button can re-title it in place.
		Map generatedMap;
		MapGenerationArgs generatedArgs;

		// User-chosen map name (via Rename). Null/empty falls back to the generator's default title.
		string customTitle;

		// On-disk path the generated map was last written to, so a rename can move the old file.
		string currentMapPath;

		volatile bool failed;
		volatile uint generationCounter = 0;
		volatile uint lastGeneration = 0;

		bool IsGenerating => lastGeneration != generationCounter;

		// A map whose preview is hidden from the map chooser must not leak its seed, or anyone could
		// regenerate it with the preview visible. The seed field and the blueprint export follow this.
		bool PreviewIsVisible => generationArgs.PreviewVisibility.HasFlag(MapGenerationArgs.PreviewVisibilityFlags.MapChooser);

		[ObjectCreator.UseCtor]
		internal MapGeneratorLogic(Widget widget, ModData modData, MapGenerationArgs initialGeneratedMap, Action<MapGenerationArgs, IReadWritePackage> onGenerate)
		{
			this.modData = modData;
			this.onGenerate = onGenerate;
			parentWidget = widget.Parent;

			generators = modData.DefaultRules.Actors[SystemActors.EditorWorld].TraitInfos<IEditorMapGeneratorInfo>().ToList();

			// Build the unified tileset list spanning every generator. Each tileset maps to the first
			// generator (in rules order) that supports it, so dedicated terrain generators (Classic/D2k)
			// own their tilesets and ClearMapGenerator - listed last - only owns tilesets nothing else covers.
			var seenTilesets = new HashSet<string>();
			validTerrainInfos = [];
			foreach (var g in generators)
				foreach (var t in g.Tilesets)
					if (seenTilesets.Add(t))
						validTerrainInfos.Add(modData.DefaultTerrainInfo[t]);

			generator = generators[0];
			preview = widget.Get<GeneratedMapPreviewWidget>("PREVIEW");

			widget.Get("ERROR").IsVisible = () => failed;

			var title = new CachedTransform<string, string>(id => FluentProvider.GetMessage(id));
			var previewTitleLabel = widget.Get<LabelWidget>("TITLE");
			previewTitleLabel.GetText = () =>
			{
				// Once a map has settled, show the user-chosen name (if any); otherwise fall back to
				// the live status text (Generating.../error) or the default random-map title.
				if (!IsGenerating && !failed && !string.IsNullOrWhiteSpace(customTitle))
					return customTitle;

				return title.Update(IsGenerating ? Generating : failed ? GenerationFailed : RandomMap);
			};

			var previewDetailsLabel = widget.GetOrNull<LabelWidget>("DETAILS");
			if (previewDetailsLabel != null)
			{
				// The default "Conquest" label is hardcoded in Map.cs
				var desc = new CachedTransform<int, string>(p => "Conquest " + FluentProvider.GetMessage(Players, "players", p));
				previewDetailsLabel.GetText = () => desc.Update(generator.GetPlayerCount(generationArgs));
				previewDetailsLabel.IsVisible = () => !failed;
			}

			var previewAuthorLabel = widget.GetOrNull<LabelWithTooltipWidget>("AUTHOR");
			if (previewAuthorLabel != null)
			{
				var desc = new CachedTransform<IEditorMapGeneratorInfo, string>(
					g => FluentProvider.GetMessage(CreatedBy, "author", FluentProvider.GetMessage(g.Name)));
				previewAuthorLabel.GetText = () => desc.Update(generator);
				previewAuthorLabel.IsVisible = () => !failed;
			}

			var previewSizeLabel = widget.GetOrNull<LabelWidget>("SIZE");
			if (previewSizeLabel != null)
			{
				var desc = new CachedTransform<Size, string>(MapChooserLogic.MapSizeLabel);
				previewSizeLabel.IsVisible = () => !failed;
				previewSizeLabel.GetText = () => desc.Update(generationArgs.Size);
			}

			optionsPanel = widget.Get<ScrollPanelWidget>("OPTIONS_PANEL");
			checkboxOptionTemplate = optionsPanel.Get<Widget>("CHECKBOX_TEMPLATE");
			textOptionTemplate = optionsPanel.Get<Widget>("TEXT_TEMPLATE");
			dropdownOptionTemplate = optionsPanel.Get<Widget>("DROPDOWN_TEMPLATE");

			// As DROPDOWN_TEMPLATE, but its button carries a tooltip - used for the tileset, whose
			// descriptive names can overflow the dropdown and need the full text available on hover.
			var dropdownTooltipTemplate = optionsPanel.GetOrNull<Widget>("DROPDOWN_TOOLTIP_TEMPLATE") ?? dropdownOptionTemplate;
			optionsPanel.Layout = new GridLayout(optionsPanel);

			// Tileset and map size are handled outside the generator logic so must be created manually
			var tilesetLabel = FluentProvider.GetMessage(Tileset);
			tilesetOption = dropdownTooltipTemplate.Clone();
			tilesetOption.Get<LabelWidget>("LABEL").GetText = () => tilesetLabel;

			var label = new CachedTransform<ITerrainInfo, string>(ti => FluentProvider.GetMessage(ti.Name));
			var tilesetDropdown = tilesetOption.Get<DropDownButtonWidget>("DROPDOWN");
			var tilesetFont = Game.Renderer.Fonts[tilesetDropdown.Font];

			// The descriptive environment names can be wider than the dropdown button, so fit them
			// with an ellipsis and surface the full name as a tooltip when it doesn't fit.
			string FitTilesetName() => WidgetUtils.TruncateText(
				label.Update(selectedTerrain),
				tilesetDropdown.UsableWidth - tilesetDropdown.LeftMargin - tilesetDropdown.RightMargin,
				tilesetFont);
			tilesetDropdown.GetText = FitTilesetName;

			// Always expose the full name on hover (must be non-null - the tooltip logic splits it).
			tilesetDropdown.GetTooltipText = () => label.Update(selectedTerrain);
			tilesetDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(ITerrainInfo terrainInfo, ScrollItemWidget template)
				{
					bool IsSelected() => terrainInfo == selectedTerrain;
					void OnClick()
					{
						// Picking a tileset owned by a different generator swaps to it and gives the
						// freshly-shown options a random seed; same-generator switches keep the options.
						if (SelectTerrain(terrainInfo))
							RandomizeSeed();
						RefreshOptions();
					}

					var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
					var itemLabel = FluentProvider.GetMessage(terrainInfo.Name);
					item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
					return item;
				}

				tilesetDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", validTerrainInfos.Count * 30, validTerrainInfos, SetupItem);
			};

			var sizeLabel = FluentProvider.GetMessage(MapSize);
			sizeOption = dropdownOptionTemplate.Clone();
			sizeOption.Get<LabelWidget>("LABEL").GetText = () => sizeLabel;

			var sizeDropdown = sizeOption.Get<DropDownButtonWidget>("DROPDOWN");
			var sizeDropdownLabel = new CachedTransform<string, string>(s => FluentProvider.GetMessage(s));
			sizeDropdown.GetText = () => sizeDropdownLabel.Update(selectedSize);
			sizeDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(string size, ScrollItemWidget template)
				{
					bool IsSelected() => size == selectedSize;
					void OnClick()
					{
						selectedSize = size;
						RandomizeSize();
					}

					var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
					var label = FluentProvider.GetMessage(size);
					item.Get<LabelWidget>("LABEL").GetText = () => label;
					return item;
				}

				sizeDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", MapSizes.Count * 30, MapSizes.Keys, SetupItem);
			};

			var previewVisibilityLabel = FluentProvider.GetMessage(PreviewVisibility);
			previewVisibilityOption = dropdownOptionTemplate.Clone();
			previewVisibilityOption.Get<LabelWidget>("LABEL").GetText = () => previewVisibilityLabel;

			var previewVisibilityDropdown = previewVisibilityOption.Get<DropDownButtonWidget>("DROPDOWN");
			var previewVisibilityDropdownLabel = new CachedTransform<string, string>(s => FluentProvider.GetMessage(s));
			previewVisibilityDropdown.GetText = () => previewVisibilityDropdownLabel.Update(selectedPreviewVisibility);
			previewVisibilityDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(string visibility, ScrollItemWidget template)
				{
					bool IsSelected() => visibility == selectedPreviewVisibility;
					void OnClick()
					{
						selectedPreviewVisibility = visibility;

						// Changes to visibility should re-randomize so that it's not possible to
						// peak at a preview by changing the setting back and forth. (Which would
						// be cheating.)
						generationArgs.PreviewVisibility = PreviewVisibilities[selectedPreviewVisibility];
						RandomizeSeed();
						RandomizeSize();
						RefreshOptions();
					}

					var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
					var label = FluentProvider.GetMessage(visibility);
					item.Get<LabelWidget>("LABEL").GetText = () => label;
					return item;
				}

				previewVisibilityDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", PreviewVisibilities.Count * 30, PreviewVisibilities.Keys, SetupItem);
			};

			var generateButton = widget.Get<ButtonWidget>("BUTTON_GENERATE");

			// Cameo: generate with the current options and seed (no implicit randomization), so a
			// seed that was typed in or randomized by the user is honoured - the same seed and
			// options always produce the same map.
			generateButton.OnClick = GenerateMap;

			// Randomizes only the seed and leaves the user's other options untouched.
			// Does not generate - the user clicks Generate themselves.
			var randomizeSeedButton = widget.GetOrNull<ButtonWidget>("BUTTON_RANDOMIZE_SEED");
			if (randomizeSeedButton != null)
			{
				randomizeSeedButton.OnClick = () =>
				{
					RandomizeSeed();
					RefreshOptions();
				};
			}

			// Blueprint panel visibility is shared across IsDisabled lambdas below, so declare here.
			var blueprintPanelVisible = false;
			generateButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;
			if (randomizeSeedButton != null)
				randomizeSeedButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;

			// Lets the user give the generated map a custom name (its Title metadata).
			var renameButton = widget.GetOrNull<ButtonWidget>("BUTTON_RENAME");
			if (renameButton != null)
			{
				renameButton.OnClick = RenameMap;
				renameButton.IsDisabled = () => IsGenerating || blueprintPanelVisible;
			}

			var blueprintPanel = widget.GetOrNull<ContainerWidget>("BLUEPRINT_PANEL");
			var exportField = widget.GetOrNull<TextFieldWidget>("BLUEPRINT_EXPORT");
			var importField = widget.GetOrNull<TextFieldWidget>("BLUEPRINT_IMPORT");
			var copyButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_COPY");
			var loadButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_LOAD");
			var closeButton = widget.GetOrNull<ButtonWidget>("BLUEPRINT_CLOSE");
			var shareButton = widget.GetOrNull<ButtonWidget>("BUTTON_BLUEPRINT");

			if (blueprintPanel != null)
				blueprintPanel.IsVisible = () => blueprintPanelVisible;

			void OpenBlueprint()
			{
				if (exportField != null)
					exportField.Text = PreviewIsVisible ? BuildBlueprintCode() : string.Empty;
				if (importField != null)
					importField.Text = string.Empty;
				blueprintPanelVisible = true;
			}

			void CloseBlueprint() => blueprintPanelVisible = false;

			if (shareButton != null)
			{
				shareButton.IsDisabled = () => IsGenerating;
				shareButton.OnClick = OpenBlueprint;
			}

			DateTime? copiedAt = null;
			if (copyButton != null && exportField != null)
			{
				// A hidden-preview map's blueprint would carry its seed, so it cannot be exported.
				copyButton.IsDisabled = () => !PreviewIsVisible;
				copyButton.GetText = () =>
					copiedAt.HasValue && (DateTime.UtcNow - copiedAt.Value).TotalSeconds < 2
						? FluentProvider.GetMessage(BlueprintCopied)
						: FluentProvider.GetMessage(BlueprintCopy);
				copyButton.OnClick = () =>
				{
					var code = BuildBlueprintCode();
					exportField.Text = code;
					Game.SetClipboardText(code);
					copiedAt = DateTime.UtcNow;
				};
			}

			if (loadButton != null && importField != null)
			{
				loadButton.IsDisabled = () => string.IsNullOrWhiteSpace(importField.Text);
				loadButton.OnClick = () =>
				{
					if (TryApplyBlueprintCode(importField.Text))
					{
						RefreshOptions();
						CloseBlueprint();
						GenerateMap();
					}
				};
			}

			if (closeButton != null)
				closeButton.OnClick = CloseBlueprint;

			selectedSize = MapSizes.Keys.Skip(1).First();
			selectedPreviewVisibility = PreviewVisibilityAll;

			// The lobby preselected a generated map: restore it with the generator that made it.
			var initialGenerator = initialGeneratedMap != null
				? generators.FirstOrDefault(g => g.Type == initialGeneratedMap.Generator)
				: null;

			if (initialGenerator != null)
			{
				var sizeLimit = MapSizes.Last().Value.Y;
				var valid = initialGenerator.ValidateArgs(
					modData,
					initialGeneratedMap,
					new Size(sizeLimit, sizeLimit),
					MapGeneratorOption.VisibilityFlags.Lobby);
				if (!valid)
					initialGenerator = null;
			}

			if (initialGenerator != null)
			{
				generator = initialGenerator;
				var defaultTitle = FluentProvider.GetMessage(generator.MapTitle);

				// Make our own copy to prevent external mutation
				generationArgs = new MapGenerationArgs()
				{
					Uid = initialGeneratedMap.Uid,
					Generator = initialGeneratedMap.Generator,
					Tileset = initialGeneratedMap.Tileset,
					Size = initialGeneratedMap.Size,
					Options = initialGeneratedMap.Options.ToDictionary(),
					Title = string.IsNullOrWhiteSpace(initialGeneratedMap.Title) ? defaultTitle : initialGeneratedMap.Title,
					Author = FluentProvider.GetMessage(generator.Name)
				};

				// Keep a name the user gave this map with Rename.
				customTitle = generationArgs.Title != defaultTitle ? generationArgs.Title : null;
				selectedTerrain = modData.DefaultTerrainInfo[generationArgs.Tileset];

				var isHiddenMap = !initialGeneratedMap.PreviewVisibility.HasFlag(MapGenerationArgs.PreviewVisibilityFlags.Lobby);
				if (isHiddenMap)
				{
					// For hidden maps, don't carry over the seed and size.
					// Also, even if the map chooser could previously preview the map, set the
					// visibility to none. This makes it obvious that the visibility setting isn't
					// PreviewVisibilityAll (or PreviewVisibilityMapChooser), forcing the user to
					// pick an appropriate visibility before they start flicking through maps.
					selectedPreviewVisibility = PreviewVisibilityNone;
					generationArgs.PreviewVisibility = MapGenerationArgs.PreviewVisibilityFlags.None;
					RandomizeSeed();
					RandomizeSize();
				}
				else
				{
					SelectSizeFor(generationArgs.Size);

					var map = modData.MapCache[generationArgs.Uid];
					if (map.Status == MapStatus.Available)
					{
						preview.Update(map);
						onGenerate(generationArgs, null);
					}
				}
			}
			else
			{
				generationArgs = NewArgs(generator);
				if (!TryLoadPersistedSettings())
				{
					SelectTerrain(validTerrainInfos[0]);
					RandomizeSeed();
					RandomizeSize();
				}
			}

			RefreshOptions();

			// Cameo: never generate on open. Restored or default options wait for the user's Generate.
			initialGenerationDone = true;
		}

		public override void Tick()
		{
			if (!initialGenerationDone && !IsGenerating && parentWidget.IsVisible())
			{
				initialGenerationDone = true;
				GenerateMap();
			}
		}

		MapGenerationArgs NewArgs(IEditorMapGeneratorInfo g)
		{
			return new MapGenerationArgs
			{
				Generator = g.Type,
				Tileset = g.Tilesets.First(),
				Title = FluentProvider.GetMessage(g.MapTitle),
				Author = FluentProvider.GetMessage(g.Name),
				PreviewVisibility = generationArgs?.PreviewVisibility ?? MapGenerationArgs.PreviewVisibilityFlags.All,
				Size = generationArgs?.Size ?? default,
			};
		}

		IEditorMapGeneratorInfo GeneratorFor(string tileset)
		{
			return generators.FirstOrDefault(g => g.Tilesets.Contains(tileset)) ?? generators[0];
		}

		// Selects a tileset and switches the active generator to whichever one owns it (first match
		// in rules order). Returns true if the generator actually changed: its options are then
		// reset, so the caller decides whether to give them a fresh seed.
		bool SelectTerrain(ITerrainInfo terrainInfo)
		{
			selectedTerrain = terrainInfo;
			var match = GeneratorFor(terrainInfo.Id);
			var changed = match != generator;
			if (changed)
			{
				generator = match;
				generationArgs = NewArgs(generator);
			}

			generationArgs.Tileset = terrainInfo.Id;
			return changed;
		}

		void SelectSizeFor(Size size)
		{
			foreach (var kv in MapSizes)
				if (size.Width >= kv.Value.X && size.Width <= kv.Value.Y)
					selectedSize = kv.Key;
		}

		void RandomizeSeed()
		{
			generationArgs.Options["Seed"] = FieldSaver.FormatValue(Game.CosmeticRandom.Next());
		}

		void RandomizeSize()
		{
			var mapGrid = modData.GetOrCreate<MapGrid>();
			var sizeRange = MapSizes[selectedSize];
			var width = Game.CosmeticRandom.Next(sizeRange.X, sizeRange.Y);
			var height = mapGrid.Type == MapGridType.RectangularIsometric ? width * 2 : width;

			generationArgs.Size = new Size(width + 2, height + mapGrid.MaximumTerrainHeight * 2 + 2);
		}

		string PersistedSettingsPath => Path.Combine(Platform.SupportDir, "map-generator-" + modData.Manifest.Id + ".yaml");

		// Tileset, size and option values (incl. the seed). The same node layout serves the on-disk
		// settings and the blueprint code, so blueprints shared before the bleed sync still load.
		List<MiniYamlNode> SettingsNodes()
		{
			return
			[
				new("Tileset", generationArgs.Tileset),
				new("Size", FieldSaver.FormatValue(generationArgs.Size)),
				new("Options", new MiniYaml(null, generationArgs.Options.Select(o => new MiniYamlNode(o.Key, o.Value)))),
			];
		}

		// Applies stored or pasted settings, which are untrusted: unknown options are dropped, missing
		// ones take their defaults, and the result must pass the generator's own ValidateArgs.
		// Returns false (leaving state untouched) if there is nothing valid to apply.
		bool TryApplySettings(MiniYaml root)
		{
			var tilesetNode = root.NodeWithKeyOrDefault("Tileset");
			var sizeNode = root.NodeWithKeyOrDefault("Size");
			var optionsNode = root.NodeWithKeyOrDefault("Options");
			if (tilesetNode == null || sizeNode == null || optionsNode == null)
				return false;

			var tileset = tilesetNode.Value.Value;
			if (tileset == null || !modData.DefaultTerrainInfo.TryGetValue(tileset, out var terrain))
				return false;

			var owner = GeneratorFor(tileset);
			var stored = optionsNode.Value.Nodes.ToDictionary(n => n.Key, n => n.Value.Value);
			var candidate = new MapGenerationArgs
			{
				Generator = owner.Type,
				Tileset = tileset,
				Size = FieldLoader.GetValue<Size>("Size", sizeNode.Value.Value),
				Title = FluentProvider.GetMessage(owner.MapTitle),
				Author = FluentProvider.GetMessage(owner.Name),
				PreviewVisibility = generationArgs.PreviewVisibility,
			};

			foreach (var o in owner.Options.Where(o => o is not MapGeneratorMultiChoiceOption))
			{
				if (stored.TryGetValue(o.Id, out var value) && !string.IsNullOrEmpty(value))
					candidate.Options[o.Id] = value;
				else if (o is MapGeneratorBooleanOption bo)
					candidate.Options[o.Id] = FieldSaver.FormatValue(bo.Default);
				else if (o is MapGeneratorIntegerOption io)
					candidate.Options[o.Id] = FieldSaver.FormatValue(io.Default);
				else if (o is MapGeneratorMultiIntegerChoiceOption mio)
					candidate.Options[o.Id] = FieldSaver.FormatValue(mio.Default);
			}

			// Multi-choice defaults depend on the player count, so they are filled last.
			var playerCount = owner.GetPlayerCount(candidate);
			foreach (var mo in owner.Options.OfType<MapGeneratorMultiChoiceOption>())
			{
				var validChoices = mo.ValidChoices(terrain, playerCount);
				candidate.Options[mo.Id] = stored.TryGetValue(mo.Id, out var value) && validChoices.Contains(value)
					? value
					: mo.DefaultFor(terrain, playerCount);
			}

			var sizeLimit = MapSizes.Last().Value.Y;
			if (!owner.ValidateArgs(modData, candidate, new Size(sizeLimit, sizeLimit), MapGeneratorOption.VisibilityFlags.Lobby))
				return false;

			generator = owner;
			generationArgs = candidate;
			selectedTerrain = terrain;
			SelectSizeFor(candidate.Size);
			return true;
		}

		// Writes the current settings to disk so the generator can restore them next time it is
		// opened, surviving game restarts. The preview visibility is kept with them, so a hidden
		// map is not restored with its seed on show.
		void PersistSettings()
		{
			try
			{
				var nodes = SettingsNodes();
				nodes.Add(new MiniYamlNode("PreviewVisibility", selectedPreviewVisibility));
				File.WriteAllText(PersistedSettingsPath, nodes.WriteToString());
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to save map generator settings: {e}");
			}
		}

		bool TryLoadPersistedSettings()
		{
			try
			{
				var path = PersistedSettingsPath;
				if (!File.Exists(path))
					return false;

				var root = new MiniYaml(null, MiniYaml.FromString(File.ReadAllText(path), path));
				var visibility = root.NodeWithKeyOrDefault("PreviewVisibility")?.Value.Value;
				if (visibility != null && PreviewVisibilities.TryGetValue(visibility, out var flags))
				{
					selectedPreviewVisibility = visibility;
					generationArgs.PreviewVisibility = flags;
				}

				return TryApplySettings(root);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to load map generator settings: {e}");
				return false;
			}
		}

		string BuildBlueprintCode()
		{
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(SettingsNodes().WriteToString()));
		}

		bool TryApplyBlueprintCode(string code)
		{
			try
			{
				var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim()));
				return TryApplySettings(new MiniYaml(null, MiniYaml.FromString(yaml, "blueprint")));
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to apply blueprint code: {e}");
				return false;
			}
		}

		void RefreshOptions()
		{
			optionsPanel.RemoveChildren();
			tilesetOption.Bounds = sizeOption.Bounds = previewVisibilityOption.Bounds =
				dropdownOptionTemplate.Bounds;
			optionsPanel.AddChild(tilesetOption);
			optionsPanel.AddChild(sizeOption);
			optionsPanel.AddChild(previewVisibilityOption);

			var trueString = FieldSaver.FormatValue(true);
			var falseString = FieldSaver.FormatValue(false);
			foreach (var o in generator.Options)
			{
				// Cameo: the seed is an editable field so a known seed can be typed in - except for a
				// hidden-preview map, whose seed would reveal it.
				if (o.Id == "Seed" && !PreviewIsVisible)
					continue;

				Widget optionWidget = null;
				var hidden = !o.Visibility.HasFlag(MapGeneratorOption.VisibilityFlags.Lobby);
				switch (o)
				{
					case MapGeneratorBooleanOption bo:
					{
						if (!generationArgs.Options.ContainsKey(o.Id))
							generationArgs.Options[o.Id] = bo.Default ? trueString : falseString;

						if (hidden)
							break;

						optionWidget = checkboxOptionTemplate.Clone();
						var checkboxWidget = optionWidget.Get<CheckboxWidget>("CHECKBOX");
						var label = FluentProvider.GetMessage(bo.Label);
						checkboxWidget.GetText = () => label;
						checkboxWidget.IsChecked = () => generationArgs.Options[o.Id] == trueString;
						checkboxWidget.OnClick = () =>
							generationArgs.Options[o.Id] = generationArgs.Options[o.Id] == trueString ? falseString : trueString;
						break;
					}

					case MapGeneratorIntegerOption io:
					{
						if (!generationArgs.Options.ContainsKey(o.Id))
							generationArgs.Options[o.Id] = FieldSaver.FormatValue(io.Default);

						if (hidden)
							break;

						optionWidget = textOptionTemplate.Clone();
						var labelWidget = optionWidget.Get<LabelWidget>("LABEL");
						var label = FluentProvider.GetMessage(io.Label);
						labelWidget.GetText = () => label;
						var textFieldWidget = optionWidget.Get<TextFieldWidget>("INPUT");
						textFieldWidget.Type = TextFieldType.Integer;
						textFieldWidget.Text = generationArgs.Options[o.Id];
						textFieldWidget.OnTextEdited = () =>
						{
							var valid = int.TryParse(textFieldWidget.Text, out var intValue);
							if (valid)
							{
								// Reformat the integer to improve cross-client compatibility.
								generationArgs.Options[o.Id] = FieldSaver.FormatValue(intValue);
							}

							textFieldWidget.IsValid = () => valid;
						};

						textFieldWidget.OnEscKey = _ => { textFieldWidget.YieldKeyboardFocus(); return true; };
						textFieldWidget.OnEnterKey = _ => { textFieldWidget.YieldKeyboardFocus(); return true; };
						break;
					}

					case MapGeneratorMultiIntegerChoiceOption mio:
					{
						if (!generationArgs.Options.ContainsKey(o.Id))
							generationArgs.Options[o.Id] = FieldSaver.FormatValue(mio.Default);

						if (hidden)
							break;

						optionWidget = dropdownOptionTemplate.Clone();
						var labelWidget = optionWidget.Get<LabelWidget>("LABEL");
						var label = FluentProvider.GetMessage(mio.Label);
						labelWidget.GetText = () => label;

						var dropDownWidget = optionWidget.Get<DropDownButtonWidget>("DROPDOWN");
						dropDownWidget.GetText = () => generationArgs.Options[o.Id];
						dropDownWidget.OnMouseDown = _ =>
						{
							ScrollItemWidget SetupItem(int choice, ScrollItemWidget template)
							{
								var choiceString = FieldSaver.FormatValue(choice);
								bool IsSelected() => choiceString == generationArgs.Options[o.Id];
								void OnClick()
								{
									generationArgs.Options[o.Id] = choiceString;
									if (o.Id == "Players")
										RefreshOptions();
								}

								var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);
								var itemLabel = FieldSaver.FormatValue(choice);
								item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
								item.GetTooltipText = null;
								return item;
							}

							dropDownWidget.ShowDropDown("LABEL_DROPDOWN_WITH_TOOLTIP_TEMPLATE", 250, mio.Choices, SetupItem);
						};
						break;
					}

					case MapGeneratorMultiChoiceOption mo:
					{
						var playerCount = generator.GetPlayerCount(generationArgs);
						var validChoices = mo.ValidChoices(selectedTerrain, playerCount);
						if (!generationArgs.Options.TryGetValue(o.Id, out var option) || !validChoices.Contains(option))
							generationArgs.Options[o.Id] = mo.DefaultFor(selectedTerrain, playerCount);

						if (hidden || mo.Label == null || validChoices.Count == 0)
							break;

						optionWidget = dropdownOptionTemplate.Clone();
						var labelWidget = optionWidget.Get<LabelWidget>("LABEL");
						var label = FluentProvider.GetMessage(mo.Label);
						labelWidget.GetText = () => label;

						var labelCache = new CachedTransform<string, string>(v => FluentProvider.GetMessage(mo.Choices[v].Label + ".label"));
						var dropDownWidget = optionWidget.Get<DropDownButtonWidget>("DROPDOWN");
						dropDownWidget.GetText = () => labelCache.Update(generationArgs.Options[o.Id]);
						dropDownWidget.OnMouseDown = _ =>
						{
							ScrollItemWidget SetupItem(string choice, ScrollItemWidget template)
							{
								bool IsSelected() => choice == generationArgs.Options[o.Id];
								void OnClick() => generationArgs.Options[o.Id] = choice;

								var item = ScrollItemWidget.Setup(template, IsSelected, OnClick);

								var itemLabel = FluentProvider.GetMessage(mo.Choices[choice].Label + ".label");
								item.Get<LabelWidget>("LABEL").GetText = () => itemLabel;
								if (FluentProvider.TryGetMessage(mo.Choices[choice].Label + ".description", out var desc))
									item.GetTooltipText = () => desc;
								else
									item.GetTooltipText = null;

								return item;
							}

							dropDownWidget.ShowDropDown("LABEL_DROPDOWN_WITH_TOOLTIP_TEMPLATE", 250, validChoices, SetupItem);
						};

						break;
					}

					default:
						throw new NotImplementedException($"Unhandled MapGeneratorOption type {o.GetType().Name}");
				}

				if (optionWidget == null)
					continue;

				optionWidget.IsVisible = () => true;
				optionsPanel.AddChild(optionWidget);
			}
		}

		void GenerateMap()
		{
			// A fresh generation is a new, separate map: drop any custom name from a previous Rename so it
			// gets the default title and filename, and stop tracking the old file so a renamed-and-kept map
			// is not deleted by this generation's save.
			customTitle = null;
			currentMapPath = null;

			var currentGeneration = Interlocked.Increment(ref generationCounter);

			failed = false;
			onGenerate(null, null);
			preview.Clear();

			// Generate from a snapshot: the options panel stays live while the task runs.
			var g = generator;
			var args = new MapGenerationArgs
			{
				Generator = generationArgs.Generator,
				Tileset = generationArgs.Tileset,
				Size = generationArgs.Size,
				Options = generationArgs.Options.ToDictionary(),
				Title = FluentProvider.GetMessage(g.MapTitle),
				Author = FluentProvider.GetMessage(g.Name),
				PreviewVisibility = generationArgs.PreviewVisibility,
			};

			Task.Run(() =>
			{
				// Tasks don't run in parallel, so we may be able to cancel some outdated requests here.
				if (currentGeneration != generationCounter)
					return;

				Map map;
				try
				{
					map = g.Generate(modData, args);
				}
				catch (Exception e)
				{
					// Catch ALL exceptions: a generator that throws anything unexpected would otherwise
					// leave the UI stuck on "Generating..." forever.
					Log.Write("debug", $"Map generation failed: {e}");

					// We are the lastest generation request, mark as failed.
					if (currentGeneration == generationCounter)
					{
						lastGeneration = currentGeneration;
						failed = true;
					}

					return;
				}

				// Need to invoke widgets from the main thread.
				Game.RunAfterTick(() =>
				{
					// A newer generation will be set after us, discard.
					if (currentGeneration == generationCounter)
					{
						// Keep a reference so the Rename button can re-title this map without regenerating.
						generatedMap = map;
						generatedArgs = args;
						SaveAndPublish(map, args);
						lastGeneration = currentGeneration;
					}
				});
			});
		}

		// Turns a map name into a safe .oramap base filename (whitespace and invalid characters
		// become underscores), defaulting to "generated" for an empty or fully-stripped name.
		static string MakeMapFileName(string title)
		{
			if (string.IsNullOrWhiteSpace(title))
				return "generated";

			var invalid = Path.GetInvalidFileNameChars();
			var cleaned = new string(title.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()).Trim('.');
			return string.IsNullOrWhiteSpace(cleaned) ? "generated" : cleaned;
		}

		// Saves the map into a fresh package, writes it to the User map folder and hands it to the lobby.
		// Shared by the initial generation and the Rename button (which also renames the file on disk).
		void SaveAndPublish(Map map, MapGenerationArgs args)
		{
			var package = new ZipFileLoader.ReadWriteZipFile();
			map.Save(package);

			args.Uid = map.Uid;

			// Save into the actual User map folder the MapCache scans (version-specific
			// subdir), not the parent maps/<mod> directory which is never enumerated.
			var userMapFolder = modData.Manifest.MapFolders.FirstOrDefault(kv => kv.Value == "User").Key;
			if (userMapFolder != null)
			{
				var folderName = userMapFolder.StartsWith('~') ? userMapFolder[1..] : userMapFolder;
				var mapsDir = Platform.ResolvePath(folderName);
				Directory.CreateDirectory(mapsDir);

				// Name the file after the chosen map name so Rename renames the file on disk;
				// fall back to a fixed scratch name when the map is unnamed.
				var newPath = Path.Combine(mapsDir, MakeMapFileName(customTitle) + ".oramap");
				File.WriteAllBytes(newPath, package.GetBytes());

				// Move rather than copy: remove the previously written file when the name changed.
				if (currentMapPath != null && currentMapPath != newPath && File.Exists(currentMapPath))
					File.Delete(currentMapPath);

				currentMapPath = newPath;
			}

			preview.Update(map, args.PreviewVisibility.HasFlag(MapGenerationArgs.PreviewVisibilityFlags.MapChooser));

			// Remember these settings (incl. the seed) so reopening the generator - even
			// after restarting the game - restores them.
			PersistSettings();

			// `onGenerate` assumed to take ownership of package here.
			onGenerate(args, package);
		}

		// Prompts for a new map name and applies it as the map's Title. An empty name clears the
		// override, restoring the generator's default title.
		void RenameMap()
		{
			var current = !string.IsNullOrWhiteSpace(customTitle)
				? customTitle
				: FluentProvider.GetMessage(RandomMap);

			ConfirmationDialogs.TextInputPrompt(modData,
				RenameTitle, RenamePrompt, current,
				onAccept: name =>
				{
					name = name?.Trim();
					customTitle = string.IsNullOrEmpty(name) ? null : name;

					// Re-title the already-generated map in place (no regeneration) and republish it
					// so the lobby and the saved file pick up the new name immediately.
					if (generatedMap != null && generatedArgs != null)
					{
						var newTitle = customTitle ?? FluentProvider.GetMessage(RandomMap);
						generatedMap.Title = newTitle;
						generatedArgs.Title = newTitle;
						SaveAndPublish(generatedMap, generatedArgs);
					}
				},
				acceptText: RenameAccept);
		}
	}
}
