---
name: check-email-examples
description: Verify and update RotaryEmailForwarding README email examples against the current EmailTemplateService rendered HTML output. Use when editing email templates, route-specific email wording, recipient behavior, or the README Email Examples section, especially to catch stale plain-text examples and outdated labels such as "Specific questions".
---

# Check Email Examples

Use this skill when `EmailTemplateService` or README email samples change.

## Workflow

1. Read `code/RotaryEmailForwarding.FunctionApp/Email/EmailTemplateService.cs` before changing examples.
2. Read the existing `ReadMe.md` "Email Examples" section and preserve unrelated README edits.
3. Render or trace the route-specific bodies for district, multi-district, country, and manual-routing examples.
4. Keep examples as HTML, not legacy plain text. Include the emitted route-specific greeting, intro paragraphs, information block, routing notes when present, support paragraph, and thank-you paragraph.
5. Use emitted labels exactly. The optional question label is `Question`, producing `<strong>Question:</strong> ...`.
6. Run `scripts/check-email-examples.ps1` from this skill folder, or perform its checks manually if PowerShell is unavailable.

## Checks

- Reject stale README text such as `approximate plain-text`, ````text` email examples, and `Specific questions:`.
- Run the focused .NET behavior tests so template output assertions stay current.
- If the generated service output changes, update both the README samples and the focused tests in `code/RotaryEmailForwarding.Tests/SpecBehaviorTests.cs`.
