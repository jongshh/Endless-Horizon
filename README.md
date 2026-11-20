
# Endless Horizon

<img width="640" height="360" alt="preview1" src="https://github.com/user-attachments/assets/9f80f088-1b0e-4d69-9cb3-119067f68f87" />

**One day, I built a castle. It was a beautiful castle... until I realized I'd built it way too close to the map border.**

Ever built a beautiful colony, only to realize that the world just... ends at the edge of your map? With nothing but a gray void beyond it?

Well, not anymore! (Say goodbye to your borders!)

**Endless Horizon** extends the landscape beyond your map borders, making your colony feel like it's part of a much larger world.

Forests, mountains, rivers, coastlines, and vegetation continue beyond the playable area, gradually fading into the distance. (Just like those old RTS games)

And the best part? **It's all visual. *chuckles***

No extra playable cells, no additional pawns, and no massive maps eating away at your TPS. Hurray!!!

Just more world to look at! (And your sieges might look a lot cooler, too.)

## What does it do?

- **Extended terrain:** Mountains, forests, rocks, and other natural terrain continue beyond the map.
- **Rivers and coastlines:** Rivers keep flowing, with natural bends, branching deltas, and varying water depths. Coastlines extend too!
- **Vegetation:** Trees, plants, rocks, and other natural details use your game's existing textures and materials, including supported modded assets.
- **Dynamic environment:** Day/night lighting, seasonal vegetation, snow, and weather effects are reflected in the extended landscape.
- **Fog and clouds:** The distant landscape gradually disappears into fog instead of ending at another ugly border.
- **Customizable:** Adjust draw distance, vegetation, fog, clouds, lighting, snow, and water-depth effects to your liking.

Special maps such as underground, space, and pocket maps are excluded.

## How does it work?

Endless Horizon tries to reconstruct the surrounding terrain using the original map's generation data.

This also applies to rivers, coastlines, mountains, and vegetation.

Everything outside the border is purely visual. You can't walk there, build there, or send your pawns exploring. (Caravans still leave through the original map border!)

The extended landscape is cached and only updated when necessary, such as when terrain near the map edge changes or when seasons change.

## Performance

I made this mod because I didn't want to expand my actual map beyond 350×350 and destroy my TPS.

(It only took me 9,000+ hours of RimWorld to finally come up with this solution. lol)

I've tested it with vanilla RimWorld and my 500+ mod setup, and honestly, I could barely notice any performance difference.

Your results may vary depending on your hardware and mod setup, but if you need better FPS, try reducing the draw distance or disabling outside vegetation first.

## Can I add it to an existing save?

**Yes.**

## Can I remove it from an existing save?

**Also yes.**

## Compatibility

**Requires RimWorld 1.7 and Harmony.**

- **ReGrowth 2** — Supports modded rock graphics and variants, including rocks beneath fog of war.
- **Fog Color Changer** — Uses your map's fog color for the extended landscape.
- **Weather Reimagined** — Extends its weather overlay beyond the map.
- **Smooth Terrain** — Recognizes blended terrain when generating the surroundings.
- **Realistic Rain** — Puddles and accumulated snow remain inside the playable map.
- **RimWorld: Waves** — Extends Waves' ocean rendering beyond the map border, allowing waves to continue seamlessly across the edge.
- **RimWorld: Living Waters** — Extends rivers, lakes, marshes, cave pools, and springs beyond the map border using Living Waters' visuals.
- **Quiet Wonders** — Extends cloud shadows, fog banks, rain splashes and ripples, snowflakes, water mist, glow tides, moon paths, marsh wisps, and river flotsam beyond the map border.
- **Combat Extended** — Sire! Some bastard just shouted "CE compatible?" again!

  He's been dealt with. One, plasteel, arrow.

  TL;DR: Yes. It's a graphics mod. Why wouldn't it be? Also tested.

Other modded terrain and vegetation may also work automatically, depending on how they're implemented.

## AI Assistance Disclosure

The original concept, design, and architecture are my own. AI tools were used to assist with coding and implementation.

Preview and images are 100% humanslop by me.

## Bug Reports & Feedback

**If you encounter a bug or incompatibility, please report it through the [GitHub Issues page](https://github.com/jongshh/Endless-Horizon/issues) or our [Discord server](https://discord.gg/QdjhJpj6T).**

Please include a diagnostics report when reporting an issue.

To generate one:

1. Load the save where the issue occurs.
2. Navigate to **Mod Settings → Endless Horizon → Diagnostics**.
3. The diagnostics report will be automatically copied to your clipboard.
4. Paste it into your bug report, along with a brief description of the problem and, if possible, a screenshot.

**Issues not reported through the methods above may not receive quick support.**
