import { lazy } from 'react'

export const LoginPage = lazy(() => import('../features/auth/LoginPage').then((module) => ({ default: module.LoginPage })))
export const DashboardPage = lazy(() => import('../features/dashboard/DashboardPage').then((module) => ({ default: module.DashboardPage })))
export const LegacyModulePage = lazy(() => import('../features/legacy/LegacyModulePage').then((module) => ({ default: module.LegacyModulePage })))
export const PurchaseOrdersPage = lazy(() => import('../features/procurement/pages/PurchaseOrdersPage').then((module) => ({ default: module.PurchaseOrdersPage })))
export const TableAdminPage = lazy(() => import('../features/field-admin/TableAdminPage').then((module) => ({ default: module.TableAdminPage })))
export const UserAdminPage = lazy(() => import('../features/user-admin/UserAdminPage').then((module) => ({ default: module.UserAdminPage })))
export const ProfilePage = lazy(() => import('../features/settings/ProfilePage').then((module) => ({ default: module.ProfilePage })))
