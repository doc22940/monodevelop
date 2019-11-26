﻿//
// Scaffolder.cs
//
// Author:
//       jasonimison <jaimison@microsoft.com>
//
// Copyright (c) 2019 Microsoft
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Threading;
using System.Threading.Tasks;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using Xwt;
using Xwt.Drawing;

namespace MonoDevelop.AspNetCore.Scaffolding
{
	class ScaffolderTemplateSelectPage : ScaffolderWizardPageBase
	{
		ListBox listBox;
		ScaffolderArgs args;
		int selectedRow;

		public event EventHandler ScaffolderSelected;
		public ScaffolderTemplateSelectPage (ScaffolderArgs args) : base (args)
		{
			this.SubSubTitle = GettextCatalog.GetString ("Select Scaffolder");
			this.args = args;
		}

		Lazy<ScaffolderBase []> GetScaffolders ()
		{
			var scaffolders = new ScaffolderBase [] {
				new EmptyMvcControllerScaffolder(args),
				new MvcControllerWithActionsScaffolder(args),
				new EmptyApiControllerScaffolder(args),
				new ApiControllerWithActionsScaffolder(args),
				new ApiControllerEntityFrameworkScaffolder(args),
				new RazorPageScaffolder(args),
				new RazorPageEntityFrameworkScaffolder(args),
				new RazorPageEntityFrameworkCrudScaffolder(args)
			};
			return new Lazy<ScaffolderBase []> (() => scaffolders);
		}

		protected override Widget GetMainControl ()
		{
			var icon = new DataField<Image> ();
			var name = new DataField<string> ();

			var listStore = new ListStore (icon, name);

			var scaffolders = GetScaffolders ().Value;

			var png = ImageService.GetIcon ("md-html-file-icon", Gtk.IconSize.Dnd);
			var selectedPng = png.WithStyles ("sel");

			foreach (var scaffolder in scaffolders) {
				var row = listStore.AddRow ();

				listStore.SetValue (row, icon, png);
				listStore.SetValue (row, name, scaffolder.Name);
			}

			listBox = new ListBox ();
			listBox.Views.Add (new ImageCellView (icon));
			listBox.Views.Add (new TextCellView (name));

			listBox.DataSource = listStore;
			listBox.HeightRequest = 300;
			listBox.WidthRequest = 300;
			listBox.SelectionChanged += (sender, e) => {
				Args.Scaffolder = scaffolders [listBox.SelectedRow];
				listStore.SetValue (selectedRow, icon, png);
				listStore.SetValue (listBox.SelectedRow, icon, selectedPng);
				selectedRow = listBox.SelectedRow;
			};
			listBox.RowActivated += (sender, e) => ScaffolderSelected?.Invoke (sender, e);
			listBox.SelectRow (0);
			listBox.FocusedRow = 0;
			listBox.SetFocus ();
			return listBox;
		}
    }
}
