﻿namespace Menees.Analyzers
{
	#region Using Directives

	using System;
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using System.Linq;
	using System.Threading;
	using Microsoft.CodeAnalysis;
	using Microsoft.CodeAnalysis.CSharp;
	using Microsoft.CodeAnalysis.CSharp.Syntax;
	using Microsoft.CodeAnalysis.Diagnostics;
	using Microsoft.CodeAnalysis.Text;

	#endregion

	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class Men002LineTooLong : DiagnosticAnalyzer
	{
		#region Public Constants

		public const string DiagnosticId = "MEN002";

		// Use a distinct ID for the notify rule so its severity can be configured independently by a user.
		public const string DiagnosticIdNotify = "MEN002A";

		#endregion

		#region Private Data Members

		private static readonly LocalizableString Title =
			new LocalizableResourceString(nameof(Resources.Men002Title), Resources.ResourceManager, typeof(Resources));

		private static readonly LocalizableString TitleNotify =
			new LocalizableResourceString(nameof(Resources.Men002TitleNotify), Resources.ResourceManager, typeof(Resources));

		private static readonly LocalizableString MessageFormat =
			new LocalizableResourceString(nameof(Resources.Men002MessageFormat), Resources.ResourceManager, typeof(Resources));

		private static readonly LocalizableString MessageFormatNotify =
			new LocalizableResourceString(nameof(Resources.Men002MessageFormatNotify), Resources.ResourceManager, typeof(Resources));

		private static readonly LocalizableString Description =
			new LocalizableResourceString(nameof(Resources.Men002Description), Resources.ResourceManager, typeof(Resources));

		private static readonly DiagnosticDescriptor Rule =
			new(DiagnosticId, Title, MessageFormat, Rules.Layout, DiagnosticSeverity.Warning, Rules.EnabledByDefault, Description);

		private static readonly DiagnosticDescriptor RuleNotify =
			new(DiagnosticIdNotify, TitleNotify, MessageFormatNotify, Rules.Layout, DiagnosticSeverity.Info, Rules.DisabledByDefault, Description);

		private Settings settings;

		#endregion

		#region Public Properties

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, RuleNotify);

		#endregion

		#region Public Methods

		public override void Initialize(AnalysisContext context)
		{
			context.RegisterCompilationStartAction(startContext => { this.settings = Settings.Cache(startContext); });
			context.RegisterSyntaxTreeActionHonorExclusions(this.HandleSyntaxTree);
		}

		#endregion

		#region Private Methods

		private static (int DisplayLength, int NotifyIndex, int MaxIndex) GetLineDetails(
			string text, int tabSize, int notifyLength, int maxLength)
		{
			const int NonIndex = -1;
			int notifyIndex = NonIndex;
			int maxIndex = NonIndex;

			// Note: This code was started from HighlightClassifier.CheckExcessLength in Menees VS Tools.
			//
			// Even if tabs are normally converted to spaces, we can still encounter tabs that we need to expand
			// (e.g., if they load a file that contains tabs).  So we have to scan the whole line to calculate
			// the correct visible length (i.e., column-based) based on tabs expanded to the next tab stop.
			int displayLength = 0;
			int textLength = text.Length;
			int textIndex;
			for (textIndex = 0; textIndex < textLength; textIndex++)
			{
				if (text[textIndex] == '\t')
				{
					// A tab always takes up at least one column and up to TabSize columns.
					// We just need to add the number of columns to get to the next tab stop.
					displayLength += tabSize - (displayLength % tabSize); // Always in [1, TabSize] range.
				}
				else
				{
					displayLength++;
				}

				if (displayLength > notifyLength && notifyIndex == NonIndex)
				{
					notifyIndex = textIndex;
				}

				if (displayLength > maxLength && maxIndex == NonIndex)
				{
					maxIndex = textIndex;
				}
			}

			return (displayLength, notifyIndex, maxIndex);
		}

		private void HandleSyntaxTree(SyntaxTreeAnalysisContext context)
		{
			if (context.Tree.TryGetText(out SourceText text))
			{
				int tabSize = this.settings.TabSize;
				int notifyLength = this.settings.NotifyLineColumns;
				int maxLength = this.settings.MaxLineColumns;

				foreach (TextLine line in text.Lines)
				{
					// We only need to calculate the line's full display length (i.e., with tabs expanded to the next tab stops)
					// if the display length could possibly be greater than the limit.  If every character in the line is full tab size
					// and it's still an acceptable length, then we can skip calling TextLine.ToString() and doing the expensive calc.
					TextSpan lineSpan = line.Span;
					if (lineSpan.Length * tabSize > Math.Min(notifyLength, maxLength))
					{
						string lineText = line.ToString();
						(int displayLength, int notifyIndex, int maxIndex) = GetLineDetails(lineText, tabSize, notifyLength, maxLength);

						// README.md documents that MEN002 takes precedence over MEN002A, so check max length first.
						// The expectation is that MEN002A's NotifyLineColumns should be strictly less than MEN002's MaxLineColumns
						// if MEN002A is enabled. By default NotifyLineColumns == MaxLineColumns, so we don't add overhead here for
						// MEN002A (since it's disabled by default).
						if (displayLength > maxLength)
						{
							TryReport(Rule, context, lineSpan, lineText, displayLength, maxLength, maxIndex);
						}
						else if (displayLength > notifyLength)
						{
							TryReport(RuleNotify, context, lineSpan, lineText, displayLength, notifyLength, notifyIndex);
						}
					}
				}
			}
		}

		private void TryReport(
			DiagnosticDescriptor descriptor,
			SyntaxTreeAnalysisContext context,
			TextSpan lineSpan,
			string lineText,
			int displayLength,
			int thresholdLength,
			int thresholdIndex)
		{
			bool report = true;
			if (this.settings.AllowLongUriLines)
			{
				// Ignore if the whole line minus comment delimiters passes Uri.TryCreate(absolute) (e.g., for http or UNC URLs).
				string scrubbed = lineText.Trim();
				if (scrubbed.StartsWith("//"))
				{
					scrubbed = scrubbed.Substring(2).Trim();
				}
				else if (scrubbed.StartsWith("/*") && scrubbed.EndsWith("*/"))
				{
					scrubbed = scrubbed.Substring(2, scrubbed.Length - 4).Trim();
				}

				report = !Uri.TryCreate(scrubbed, UriKind.Absolute, out _);
			}

			if (report)
			{
				TextSpan excess = TextSpan.FromBounds(lineSpan.Start + thresholdIndex, lineSpan.End);
				Location location = Location.Create(context.Tree, excess);
				context.ReportDiagnostic(Diagnostic.Create(descriptor, location, thresholdLength, displayLength));
			}
		}

		#endregion
	}
}