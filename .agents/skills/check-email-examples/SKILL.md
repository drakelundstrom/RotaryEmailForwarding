---
name: check-email-examples
description: Verify and update RotaryEmailForwarding README email examples against the current EmailTemplateService rendered HTML output. Use when editing email templates, route-specific email wording, recipient behavior, or the README Email Examples section, especially to catch stale plain-text examples and outdated labels such as "Specific questions".
---

# Check Email Examples

Use this skill when `EmailTemplateService` or README email samples change.

## Workflow

1. Read `code/RotaryEmailForwarding.FunctionApp/Email/EmailTemplateService.cs` before changing examples.
2. Read the existing `ReadMe.md` "Email Examples" section and preserve unrelated README edits.
3. Render or trace the route-specific bodies for district, multi-district, country, and manual-routing examples, plus any submitter-specific variations such as Rotarian wording.
4. Show every sample body twice:
   - Under `#### HTML source`, include the literal output in an `html` fenced code block.
   - Under `#### Rendered body`, repeat the exact same HTML between `<!-- email-example-rendered:start -->` and `<!-- email-example-rendered:end -->` so Markdown renderers display the email preview.
5. Keep the HTML source and rendered body byte-for-byte equivalent after line-ending normalization. Include the emitted route-specific greeting, bold-underlined audience labels, intro paragraphs, information block, routing notes when present, support paragraph, and thank-you paragraph in both views.
6. Use emitted labels exactly. The optional question label is `Question`, producing `<strong>Question:</strong> ...`.
7. Run `scripts/check-email-examples.ps1` from this skill folder, or perform its checks manually if PowerShell is unavailable.

## Checks

- Reject stale README text such as `approximate plain-text`, ````text` email examples, and `Specific questions:`.
- Require a paired HTML source and rendered body for every sample email and fail when their contents differ.
- Run the focused .NET behavior tests so template output assertions stay current.
- If the generated service output changes, update both the README samples and the focused tests in `code/RotaryEmailForwarding.Tests/SpecBehaviorTests.cs`.
