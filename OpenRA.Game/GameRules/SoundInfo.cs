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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace OpenRA.GameRules
{
	public class SoundInfo
	{
		public readonly FrozenDictionary<string, ImmutableArray<string>> Variants = FrozenDictionary<string, ImmutableArray<string>>.Empty;
		public readonly FrozenDictionary<string, ImmutableArray<string>> Prefixes = FrozenDictionary<string, ImmutableArray<string>>.Empty;
		public readonly FrozenDictionary<string, ImmutableArray<string>> Voices = FrozenDictionary<string, ImmutableArray<string>>.Empty;
		public readonly FrozenDictionary<string, ImmutableArray<string>> Notifications = FrozenDictionary<string, ImmutableArray<string>>.Empty;
		public readonly string DefaultVariant = ".aud";
		public readonly string DefaultPrefix = "";
		public readonly FrozenSet<string> DisableVariants = [];
		public readonly FrozenSet<string> DisablePrefixes = [];

		// Minimum delay in milliseconds between consecutive queued speech notifications
		// (0 = play back-to-back). Consulted only on the speech-notification path.
		public readonly int QueueDelay = 0;

		// A queued speech notification waiting longer than this many milliseconds is dropped
		// instead of played. Consulted only on the speech-notification path.
		public readonly int QueueExpiry = 5000;

		public readonly Lazy<FrozenDictionary<string, SoundPool>> VoicePools;
		public readonly Lazy<FrozenDictionary<string, SoundPool>> NotificationsPools;

		public SoundInfo(MiniYaml y)
		{
			FieldLoader.Load(this, y);

			VoicePools = Exts.Lazy(() => Voices.ToFrozenDictionary(a => a.Key, a => new SoundPool(1f, SoundPool.DefaultInterruptType, 0, false, a.Value)));
			NotificationsPools = Exts.Lazy(() => ParseSoundPool(y, "Notifications"));
		}

		static FrozenDictionary<string, SoundPool> ParseSoundPool(MiniYaml y, string key)
		{
			var classifiction = y.NodeWithKey(key);
			var ret = new Dictionary<string, SoundPool>(classifiction.Value.Nodes.Length);
			foreach (var t in classifiction.Value.Nodes)
			{
				var volumeModifier = 1f;
				var volumeModifierNode = t.Value.NodeWithKeyOrDefault(nameof(SoundPool.VolumeModifier));
				if (volumeModifierNode != null)
					volumeModifier = FieldLoader.GetValue<float>(volumeModifierNode.Key, volumeModifierNode.Value.Value);

				var interruptType = SoundPool.DefaultInterruptType;
				var interruptTypeNode = t.Value.NodeWithKeyOrDefault(nameof(SoundPool.InterruptType));
				if (interruptTypeNode != null)
					interruptType = FieldLoader.GetValue<SoundPool.InterruptType>(interruptTypeNode.Key, interruptTypeNode.Value.Value);

				var cooldown = 0;
				var cooldownNode = t.Value.NodeWithKeyOrDefault(nameof(SoundPool.Cooldown));
				if (cooldownNode != null)
					cooldown = FieldLoader.GetValue<int>(cooldownNode.Key, cooldownNode.Value.Value);

				var priority = false;
				var priorityNode = t.Value.NodeWithKeyOrDefault(nameof(SoundPool.Priority));
				if (priorityNode != null)
					priority = FieldLoader.GetValue<bool>(priorityNode.Key, priorityNode.Value.Value);

				var names = FieldLoader.GetValue<ImmutableArray<string>>(t.Key, t.Value.Value);
				var sp = new SoundPool(volumeModifier, interruptType, cooldown, priority, names);
				ret.Add(t.Key, sp);
			}

			return ret.ToFrozenDictionary();
		}
	}

	public class SoundPool
	{
		public enum InterruptType { DoNotPlay, Interrupt, Overlap }
		public const InterruptType DefaultInterruptType = InterruptType.DoNotPlay;
		public readonly float VolumeModifier;
		public readonly InterruptType Type;

		// Cooldown and Priority are consulted only on the speech-notification path
		// (Sound.PlayPredefined / TickSpeechNotifications); they are ignored for sound-effect
		// pools and unit-voice pools.
		// Cooldown: minimum milliseconds between accepting this notification (0 = none).
		// Priority: when true the notification bypasses the one-at-a-time speech queue and plays
		// immediately, so important outcome/alert lines are never delayed or pruned behind chatter.
		public readonly int Cooldown;
		public readonly bool Priority;
		readonly ImmutableArray<string> clips;
		readonly List<string> liveclips = [];

		public SoundPool(float volumeModifier, InterruptType interruptType, int cooldown, bool priority, ImmutableArray<string> clips)
		{
			VolumeModifier = volumeModifier;
			Type = interruptType;
			Cooldown = cooldown;
			Priority = priority;
			this.clips = clips;
		}

		public ImmutableArray<string> Clips => clips;

		public string GetNext()
		{
			if (liveclips.Count == 0)
				liveclips.AddRange(clips);

			// Avoid crashing if there's no clips at all
			if (liveclips.Count == 0)
				return null;

			var i = Game.CosmeticRandom.Next(liveclips.Count);
			var s = liveclips[i];
			liveclips.RemoveAt(i);
			return s;
		}
	}
}
