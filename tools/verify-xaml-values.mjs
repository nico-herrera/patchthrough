#!/usr/bin/env node
// Checks that every design token a XAML attribute consumes has the type the
// property needs.
//
// `CornerRadius="7"` works in XAML because a string goes through the
// property's type converter. `CornerRadius="{x:Static th:PT+M.RowRadius}"`
// does not: x:Static assigns the value directly, no converter runs, and a
// raw double is not a CornerRadius. The assignment compiles cleanly on every
// platform and throws XamlParseException when the BAML loads, which is to
// say on Windows only, at startup. The first CI render of the interface
// found exactly that, so this check exists to find the next one on macOS.
//
// It also refuses a markup extension embedded inside a longer attribute
// string, like Margin="0,{x:Static …},0,8". XAML treats a value that does
// not start with `{` as a plain string, so the "extension" reaches the type
// converter as literal text and fails the same way.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';

const repoRoot = path.resolve(import.meta.dirname, '..');
const appRoot = path.join(repoRoot, 'windows/src/Patchthrough.App');
const themePath = path.join(appRoot, 'Theme/PT.cs');

// Properties whose values x:Static must match exactly, and the type each
// needs. Width/Height and friends take plain doubles and need no rule.
const REQUIRED = {
  Margin: 'Thickness',
  Padding: 'Thickness',
  BorderThickness: 'Thickness',
  CornerRadius: 'CornerRadius',
};
// Grid definitions take GridLength, and a double is the natural mistake.
const GRID = /<(?:ColumnDefinition[^>]*\bWidth|RowDefinition[^>]*\bHeight)="\{x:Static th:PT\+(\w+)\.(\w+)\}"/g;

function walk(directory, extension, found = []) {
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    if (entry === 'obj' || entry === 'bin') continue;
    if (statSync(full).isDirectory()) walk(full, extension, found);
    else if (entry.endsWith(extension)) found.push(full);
  }
  return found;
}

/** Token types per PT nested class, read from the theme source itself. */
function readTokenTypes() {
  const text = readFileSync(themePath, 'utf8');
  const types = new Map();
  let currentClass = null;
  for (const line of text.split('\n')) {
    const classDeclaration = /^\s*public static class (\w+)/.exec(line);
    if (classDeclaration) {
      currentClass = classDeclaration[1];
      continue;
    }
    if (!currentClass) continue;
    const constant = /^\s*public const (\w+) (\w+)\s*=/.exec(line);
    if (constant) types.set(`${currentClass}.${constant[2]}`, constant[1]);
    const field = /^\s*public static readonly ([\w.]+) (\w+)\s*=/.exec(line);
    if (field) types.set(`${currentClass}.${field[2]}`, field[1]);
  }
  return types;
}

function main() {
  const types = readTokenTypes();
  const problems = [];
  const files = walk(appRoot, '.xaml');

  for (const file of files) {
    const name = path.basename(file);
    const text = readFileSync(file, 'utf8');

    // A markup extension that is not the whole attribute value is a string.
    for (const match of text.matchAll(/(\w+)="([^"{][^"]*\{x:Static[^"]*)"/g)) {
      problems.push(
        `${name}: ${match[1]}="${match[2]}" embeds x:Static in a string, `
        + 'so the type converter receives it as literal text');
    }

    // Plain attributes: Prop="{x:Static th:PT+C.Name}".
    for (const match of text.matchAll(/(\w+)="\{x:Static th:PT\+(\w+)\.(\w+)\}"/g)) {
      report(name, match[1], match[2], match[3]);
    }

    // Setter form, attribute order free: <Setter Property="Prop" Value="…"/>.
    for (const match of text.matchAll(
      /<Setter(?=[^>]*\bProperty="(\w+)")(?=[^>]*\bValue="\{x:Static th:PT\+(\w+)\.(\w+)\}")[^>]*>/g)) {
      report(name, match[1], match[2], match[3]);
    }

    for (const match of text.matchAll(GRID)) {
      const token = `${match[1]}.${match[2]}`;
      const type = types.get(token);
      if (type !== undefined && type !== 'GridLength') {
        problems.push(
          `${name}: a grid definition uses PT.${token}, which is ${type}; `
          + 'it needs GridLength (see PT.G)');
      }
    }
  }

  function report(file, property, tokenClass, tokenName) {
    const needed = REQUIRED[property];
    if (needed === undefined) return;
    const token = `${tokenClass}.${tokenName}`;
    const type = types.get(token);
    if (type === undefined || type === needed) return; // unknown: not ours to judge
    problems.push(
      `${file}: ${property} uses PT.${token}, which is ${type}; `
      + `${property} needs ${needed}`);
  }

  if (problems.length > 0) {
    console.error('XAML values whose token type cannot load:\n');
    for (const problem of problems.sort()) console.error(`  ${problem}`);
    console.error(
      '\nx:Static assigns without a type converter, so each of these throws '
      + 'XamlParseException when the BAML loads, on Windows only.');
    process.exit(1);
  }
  console.log(`verify-xaml-values: ${files.length} XAML files, every token type loads`);
}

main();
