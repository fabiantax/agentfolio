/*
 * saved-posts.test.ts — fixture-backed tests for the saved-posts adapter.
 * Fixtures are SYNTHETIC (hand-written, modeled on LinkedIn DOM — see the
 * comment header in each fixture file). Parsed with linkedom, no browser.
 */
import { readFileSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";
import { describe, expect, it } from "vitest";
import { parseHTML } from "linkedom";
import {
  CARD_SELECTORS,
  harvestSavedPosts,
  extractSavedPost,
} from "./saved-posts";

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES = join(__dirname, "fixtures");

function loadFixture(name: string): Document {
  const html = readFileSync(join(FIXTURES, name), "utf-8");
  const { document } = parseHTML(html);
  return document as unknown as Document;
}

describe("harvestSavedPosts — synthetic saved-posts.html fixture", () => {
  const doc = loadFixture("saved-posts.html");

  it("finds cards via the entity-result primary selector", () => {
    expect(CARD_SELECTORS[0]).toBe("div.entity-result");
    expect(doc.querySelectorAll("div.entity-result").length).toBe(3);
  });

  it("harvests exactly 3 valid posts", () => {
    const posts = harvestSavedPosts(doc);
    expect(posts.length).toBe(3);
  });

  it("canonicalizes post URLs (strips li_ucat/trackingId/origin/utm/eid)", () => {
    const posts = harvestSavedPosts(doc);
    expect(posts[0]!.postUrl).toBe(
      "https://www.linkedin.com/feed/update/urn:li:activity:7123456789012345678",
    );
    expect(posts[1]!.postUrl).toBe(
      "https://www.linkedin.com/posts/marcuschen/llamacpp-rocm-activity-7198765432109876543-ef12",
    );
    expect(posts[2]!.postUrl).toBe(
      "https://www.linkedin.com/feed/update/urn:li:ugcPost:7188223344556677889",
    );
  });

  it("extracts title, author, savedAt, excerpt from entity-result cards", () => {
    const p = harvestSavedPosts(doc)[0]!;
    expect(p.title).toContain("Senior Rust engineer at Acme");
    expect(p.authorName).toBe("Dr. Elena Vasquez");
    expect(p.savedAt).toBe("Saved 2 weeks ago");
    expect(p.excerpt).toContain("frontier-batched PageRank");
  });

  it("extracts from the feed-shared variant card", () => {
    const p = harvestSavedPosts(doc)[1]!;
    expect(p.title).toContain("Benchmark thread");
    expect(p.authorName).toBe("Marcus Chen");
    expect(p.excerpt).toContain("Strix Halo");
  });

  it("extracts from a minimal rotated-class card via fallbacks", () => {
    const p = harvestSavedPosts(doc)[2]!;
    expect(p.title).toContain("Q3 infra postmortem");
    expect(p.authorName).toBe("Priya Raghavan");
  });

  it("never captures profile image URLs", () => {
    const posts = harvestSavedPosts(doc);
    const json = JSON.stringify(posts);
    expect(json).not.toContain("media.licdn.example");
    expect(json).not.toContain("profile");
    expect(json).not.toMatch(/shrink_800_800/);
  });
});

describe("harvestSavedPosts — degenerate fixture", () => {
  const doc = loadFixture("saved-posts-degenerate.html");

  it("skips cards without a post URL, keeps valid ones, never throws", () => {
    const posts = harvestSavedPosts(doc);
    expect(posts.length).toBe(1);
    expect(posts[0]!.postUrl).toBe(
      "https://www.linkedin.com/feed/update/urn:li:activity:7000111222333444555",
    );
  });

  it("never leaks the avatar image URL", () => {
    expect(JSON.stringify(harvestSavedPosts(doc))).not.toContain("profile-photo");
  });
});

describe("extractSavedPost", () => {
  it("returns all-null fields for an empty card without throwing", () => {
    const { document } = parseHTML("<div class='entity-result'></div>");
    const card = document.querySelector("div.entity-result")!;
    const p = extractSavedPost(card as unknown as Element);
    expect(p.postUrl).toBeNull();
    expect(p.title).toBeNull();
    expect(p.authorName).toBeNull();
    expect(p.savedAt).toBeNull();
    expect(p.excerpt).toBeNull();
  });
});
