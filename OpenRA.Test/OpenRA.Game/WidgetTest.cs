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

using NUnit.Framework;
using OpenRA.Widgets;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class WidgetTest
	{
		sealed class SimpleWidget : Widget { }

		sealed class DetachingWidget : Widget
		{
			public Widget Target;
			public int RemovedCount;
			public int HiddenCount;

			public override void Removed()
			{
				RemovedCount++;
				Target?.Parent?.RemoveChild(Target);
				base.Removed();
			}

			public override void Hidden()
			{
				HiddenCount++;
				Target?.Parent?.HideChild(Target);
				base.Hidden();
			}
		}

		[TestCase(TestName = "RemoveChildren tolerates children detaching siblings")]
		public void RemoveChildrenWithReentrantRemoval()
		{
			var root = new SimpleWidget();
			var first = new DetachingWidget();
			var middle = new SimpleWidget();
			var last = new DetachingWidget { Target = middle };
			first.Target = middle;

			root.AddChild(first);
			root.AddChild(middle);
			root.AddChild(last);
			root.RemoveChildren();

			Assert.That(root.Children, Is.Empty);
			Assert.That(first.RemovedCount, Is.EqualTo(1));
			Assert.That(last.RemovedCount, Is.EqualTo(1));
		}

		[TestCase(TestName = "Removed tolerates children detaching siblings")]
		public void RemovedWithReentrantRemoval()
		{
			var root = new SimpleWidget();
			var removed = new SimpleWidget();
			var detacher = new DetachingWidget { Target = removed };

			root.AddChild(removed);
			root.AddChild(detacher);
			root.Removed();

			Assert.That(detacher.RemovedCount, Is.EqualTo(1));
		}

		[TestCase(TestName = "Hidden tolerates children detaching siblings")]
		public void HiddenWithReentrantRemoval()
		{
			var root = new SimpleWidget();
			var hidden = new SimpleWidget();
			var detacher = new DetachingWidget { Target = hidden };

			root.AddChild(hidden);
			root.AddChild(detacher);
			root.Hidden();

			Assert.That(detacher.HiddenCount, Is.EqualTo(1));
		}
	}
}
