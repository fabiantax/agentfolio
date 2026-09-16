---
version: alpha
name: AgentFolio Engineered
description: Dark, precise developer-tool aesthetic for a portfolio chat product; engineered credibility for recruiters.
colors:
  primary: "#E6EDF3"
  secondary: "#9BA8B7"
  tertiary: "#4C8DFF"
  neutral: "#0D1117"
  surface: "#151B23"
  raised: "#1C2430"
  border: "#2A3441"
  accent: "#4C8DFF"
  success: "#3FB950"
  danger: "#F85149"
typography:
  h1:
    fontFamily: Inter
    fontSize: 1.75rem
    fontWeight: 700
    lineHeight: 1.15
    letterSpacing: "-0.02em"
  h2:
    fontFamily: Inter
    fontSize: 1.125rem
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "-0.01em"
  body-md:
    fontFamily: Inter
    fontSize: 0.9375rem
    fontWeight: 400
    lineHeight: 1.55
  body-sm:
    fontFamily: Inter
    fontSize: 0.8125rem
    fontWeight: 400
    lineHeight: 1.5
  label-xs:
    fontFamily: "JetBrains Mono"
    fontSize: 0.6875rem
    fontWeight: 500
    lineHeight: 1.4
    letterSpacing: "0.08em"
rounded:
  sm: 6px
  md: 10px
  lg: 14px
spacing:
  xs: 4px
  sm: 8px
  md: 16px
  lg: 24px
  xl: 48px
components:
  page:
    backgroundColor: "{colors.neutral}"
    textColor: "{colors.primary}"
  card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.primary}"
    rounded: "{rounded.lg}"
    padding: 24px
  card-raised:
    backgroundColor: "{colors.raised}"
    textColor: "{colors.primary}"
    rounded: "{rounded.md}"
  button-primary:
    backgroundColor: "#2B67D4"
    textColor: "#FFFFFF"
    rounded: "{rounded.md}"
    padding: 10px
  button-primary-hover:
    backgroundColor: "#2E6ADB"
    textColor: "#FFFFFF"
  button-ghost:
    backgroundColor: "{colors.raised}"
    textColor: "{colors.primary}"
    rounded: "{rounded.md}"
    padding: 8px
  button-ghost-hover:
    backgroundColor: "{colors.border}"
    textColor: "{colors.primary}"
  badge-tier:
    backgroundColor: "#0E2A13"
    textColor: "{colors.success}"
    rounded: 999px
    padding: 3px
  badge-neutral:
    backgroundColor: "{colors.raised}"
    textColor: "{colors.secondary}"
    rounded: 999px
    padding: 3px
  bubble-error:
    backgroundColor: "#3A1214"
    textColor: "{colors.danger}"
    rounded: 14px
  bubble-user:
    backgroundColor: "#12325E"
    textColor: "#E6EDF3"
    rounded: 14px
  bubble-assistant:
    backgroundColor: "{colors.raised}"
    textColor: "{colors.primary}"
    rounded: 14px
  link-citation:
    textColor: "{colors.accent}"
  input-field:
    backgroundColor: "#0B0F14"
    textColor: "{colors.primary}"
    rounded: "{rounded.md}"
    padding: 10px
---

# Overview

AgentFolio is a portfolio-chat product: recruiters ask questions, the app answers
with citations about a candidate's real work. The visual identity must read as
*engineered credibility* — the aesthetic of a well-run engineering org (Linear,
GitHub dark, Vercel), not a marketing site. Dark, calm, low-chroma surfaces; a
single blue accent reserved for interaction; monospace only for machine signals
(tier badges, labels, keys). Density is comfortable, not sparse: recruiters scan.

## Colors

- **Neutral (#0D1117) / Surface (#151B23) / Raised (#1C2430):** three stacked
  depth layers. Page < card < controls. Never more than one raise per nesting level.
- **Primary text (#E6EDF3), Secondary (#9BA8B7):** 15.3:1 and 7.2:1 on surface — AA/AAA body+meta.
- **Accent (#4C8DFF):** the ONLY interaction color (buttons, links, focus rings).
  4.6:1 on neutral as text; on buttons, white text (3.9:1 — large/bold UI text only).
- **Success (#3FB950) / Danger (#F85149):** semantic, used sparingly (tier badge,
  errors). Never decorative.

## Typography

Inter for everything human; JetBrains Mono uppercase for labels/badges (the
"machine voice"). One display size (h1 1.75rem) — this is a tool, not a landing
page; hierarchy comes from weight and color, not size inflation.

## Layout

4px base grid; 24px card padding; max content width 880px; 48px page gutters on
desktop, 16px mobile. Chat transcript is the page's center of gravity.

## Components

`button-primary` is the sole high-emphasis action (Send). `button-ghost` is every
secondary control (📎 upload, clear). Tier badge uses the mono label voice — it's
a machine signal, not prose. Bubbles: user = tinted blue (identity), assistant =
raised neutral (the machine).

## Do's and Don'ts

- Do keep one accent color; do use mono caps for machine labels; do keep 4.5:1+ contrast.
- Don't add gradients, glows, or a second accent; don't use pure black (#000) or
  pure white (#FFF) on large surfaces; don't color body text with the accent.
