---
name: erp-ui
description: Reconstruct and modernize this ERP user interface from legacy screenshots while preserving business workflows. Use when designing, implementing, reviewing, or testing ERP layouts, pages, forms, tables, dialogs, navigation, responsive behavior, or visual consistency.
---

# ERP UI modernization

## Objective

Create a professional, coherent, information-dense ERP interface for modern browsers. Treat legacy screenshots as evidence of business structure and workflow, not as a visual style to copy blindly.

## Sources of truth

Before changing UI, inspect these sources in order:

1. `ui-reference/README.md` for conventions and scope.
2. `ui-reference/inventory/pages.md` for the page catalogue and priorities.
3. Relevant images under `ui-reference/screenshots/<module>/<page>/`.
4. Matching notes under `ui-reference/annotations/` and workflows under `ui-reference/workflows/`.
5. Existing application code, components, design tokens, and established project patterns.

If evidence conflicts, preserve business behavior first, then accessibility and consistency, then visual similarity. State important assumptions; do not invent critical fields, permissions, calculations, or destructive actions.

## Required workflow

### 1. Understand before editing

- Identify the page type: list, detail, edit form, dashboard, wizard, report, or dialog.
- Extract navigation, field groups, table columns, actions, statuses, validation, dialogs, and state transitions from all available screenshots.
- Check related pages so shared elements are not designed in isolation.
- Distinguish legacy technical limitations from intentional workflow requirements.

### 2. Propose the page contract

Before implementation, define briefly:

- page purpose and primary user task;
- primary and secondary actions;
- major regions and their hierarchy;
- data density and table behavior;
- loading, empty, error, validation, disabled, and permission-denied states;
- narrow-window behavior where relevant.

For a new visual direction, implement one representative page first. Do not bulk-convert modules until the representative page has been reviewed.

### 3. Reuse the system

- Reuse existing layout, form, table, dialog, feedback, and navigation components.
- Use existing design tokens. If none exist, derive a small token set instead of scattering literal values.
- Keep spacing, typography, control heights, radii, borders, shadows, and status colors consistent.
- Prefer restrained neutral surfaces, clear hierarchy, compact forms, and readable data tables.
- Use icons only when their meaning is familiar or accompanied by text/tooltips.
- Do not introduce a new UI library or dependency without explicit approval.

### 4. ERP interaction rules

- Keep the primary task visible and minimize unnecessary navigation.
- Put page-level actions consistently; visually separate destructive actions.
- Keep filters near their results and make active filters obvious and removable.
- Tables must support readable alignment, long values, empty values, loading, no-results, pagination, and horizontal overflow where needed.
- Right-align numeric and monetary values; align decimal precision consistently.
- Never rely on color alone to communicate status.
- Preserve entered form data after recoverable validation or server errors.
- Mark required fields consistently and place validation feedback next to the affected control.
- Confirm destructive or irreversible actions with specific consequences.
- Avoid excessive cards, oversized headings, decorative gradients, glass effects, gratuitous animation, and large empty areas.

### 5. Accessibility and browser baseline

- Use semantic HTML and native controls where practical.
- Every input needs an accessible name; every icon-only action needs an accessible label.
- Ensure visible keyboard focus, logical tab order, and keyboard-operable dialogs and menus.
- Maintain sufficient contrast and usable target sizes.
- Do not encode meaning solely by color, position, or hover.
- Target current Chromium-based Edge and Chrome first; preserve standards-based compatibility with current Firefox and Safari when project requirements allow.
- Verify common office viewports, browser zoom, text expansion, long localized labels, and high-density tables.

### 6. Validate with Playwright MCP

After implementation:

1. Open the actual route in the running application.
2. Test the primary workflow and important dialogs, menus, filters, and validation.
3. Check console errors and failed application requests.
4. Capture at least 1366×768 and 1920×1080 screenshots unless the page has another explicit target.
5. Compare against the legacy reference for business completeness, not pixel identity.
6. Check clipping, overlap, horizontal scrolling, sticky regions, focus visibility, and loading/empty/error states.
7. Iterate until the page is coherent at both sizes.

Save new comparison captures under `ui-reference/reviews/<module>/<page>/` only when the user requests persistent review artifacts.

## Screenshot interpretation checklist

For every legacy screen, record or infer cautiously:

- module, page name, entry path, and screenshot state;
- global and local navigation;
- title, breadcrumbs, tabs, and grouping;
- labels, field types, required indicators, defaults, and dependencies;
- table columns, alignment, sorting, filtering, grouping, totals, and row actions;
- command priority, keyboard hints, confirmation behavior, and resulting state;
- role/permission clues;
- truncation, disabled controls, error messages, tooltips, and popups;
- uncertainties that need user verification.

## Visual quality bar

The result should feel like one product rather than independent pages. Favor clarity, predictability, scanability, and sustained daily use over novelty. A legacy screenshot is successfully modernized when users can recognize and complete the same business task faster without inheriting obsolete visual constraints.

## Completion report

When finishing UI work, summarize:

- pages/components changed;
- legacy behavior retained or intentionally improved;
- states and viewport sizes validated with Playwright;
- unresolved assumptions or missing reference captures.
