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

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>Occupants supplied by a container outside the Common assembly.</summary>
	public interface IFirePortOccupantProvider
	{
		/// <summary>Current container occupants in stable container order.</summary>
		IEnumerable<Actor> Occupants { get; }
	}
	/// <summary>Entry notification forwarded by an external occupant provider.</summary>
	public interface INotifyFirePortOccupantEntered
	{
		void OnFirePortOccupantEntered(Actor self, Actor occupant);
	}
	/// <summary>Exit notification forwarded by an external occupant provider.</summary>
	public interface INotifyFirePortOccupantExited
	{
		void OnFirePortOccupantExited(Actor self, Actor occupant);
	}

	/// <summary>An attack owner that acquires opportunity targets independently of the host AutoTarget.</summary>
	public interface IIndependentAutoTarget { }

	/// <summary>Target-specific passenger capability for bot, predictor and UI queries.</summary>
	public interface IFirePortAttack
	{
		/// <summary>Tests one occupied port at its actual offset, cone and weapon range.</summary>
		bool CanFireFromPort(int port, in Target target);
		/// <summary>True when at least one current station can fire normally.</summary>
		bool CanFireFromAnyPort(in Target target);
		/// <summary>Eligible station weapons, in increasing port order.</summary>
		IEnumerable<Armament> ArmamentsAgainst(in Target target);
		/// <summary>Read-only independent priority forecast, one target per port.</summary>
		IReadOnlyList<Target> ForecastTargets();
	}
}
