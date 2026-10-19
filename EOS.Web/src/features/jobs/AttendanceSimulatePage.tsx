import { AttendanceGenerateForm } from './AttendanceGenerateForm'

/** 考勤模拟生成（模块 180654）。 */
export function AttendanceSimulatePage() {
  return (
    <AttendanceGenerateForm
      mode="simulate"
      title="考勤模拟生成"
      description="按日期范围生成空白考勤记录，并按已批核排班的班次时间填充上下班（ON1/OUT1）。"
    />
  )
}
