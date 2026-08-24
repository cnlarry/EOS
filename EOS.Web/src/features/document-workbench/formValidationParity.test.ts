/// <reference types="node" />
import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { evaluateField } from './formValidation'

/**
 * ADR-006 决策 2.4 校验单一来源对拍矩阵（前端侧）：
 * 与 EOS.API.Tests/FormValidationParityTests.cs 消费同一份 fixture，
 * evaluateField 的判定码与规范化值必须与服务端保存管线一致。规则变更必须先改 fixture。
 */

interface ParityField {
  key: string
  dataType: string
  maxLength?: number | null
  isRequired?: boolean | null
  regex?: string | null
  precision?: number | null
  scale?: number | null
}

interface ParityCase {
  name: string
  field: ParityField
  input: string | null
  expectedCode: string
  expectedValue?: string | null
}

function findFixturePath(): string {
  let directory = dirname(fileURLToPath(import.meta.url))
  for (let depth = 0; depth < 10; depth++, directory = dirname(directory)) {
    const candidate = join(directory, 'EOS.API.Tests', 'form-validation-parity.json')
    if (existsSync(candidate)) return candidate
  }
  throw new Error('未找到 form-validation-parity.json（对拍 fixture）。')
}

function toDefinition(field: ParityField): FormFieldDefinition {
  return {
    key: field.key,
    label: `label-${field.key}`,
    dataType: field.dataType,
    displayLength: 100,
    displayFormat: null,
    isRequired: field.isRequired ?? false,
    verifyIndex: null,
    regex: field.regex ?? null,
    defaultValue: null,
    isReadonly: false,
    isVisible: true,
    onlyChoose: false,
    chooseMultiple: false,
    choosePage: null,
    choosers: [],
    isPrimaryKey: false,
    isAutoIncrement: false,
    isVirtual: false,
    isCost: false,
    isSecrecy: false,
    serverFilled: false,
    maxLength: field.maxLength ?? null,
    tabNo: 1,
    formOrder: null,
    span: 1,
    newLine: false,
    cellGroup: null,
    cellRole: 0,
    options: [],
    displayOnly: false,
    canCopy: true,
    precision: field.precision ?? null,
    scale: field.scale ?? null,
  }
}

const fixture = JSON.parse(readFileSync(findFixturePath(), 'utf-8')) as { cases: ParityCase[] }

describe('form-validation-parity（ADR-006 决策 2.4，与服务端同源 fixture）', () => {
  it.each(fixture.cases.map(parityCase => [parityCase.name, parityCase] as const))('%s', (_, parityCase) => {
    const verdict = evaluateField(toDefinition(parityCase.field), parityCase.input ?? undefined)
    expect(verdict.code, `用例「${parityCase.name}」判定不一致`).toBe(parityCase.expectedCode)
    if (parityCase.expectedCode === 'ok' && parityCase.expectedValue != null) {
      expect(verdict.value, `用例「${parityCase.name}」规范化值不一致`).toBe(parityCase.expectedValue)
    }
  })
})
