import { http } from './http'

/** REST 接口封装：返回值直接是后端 JSON。 */
export const api = {
  login: (payload) => http.post('/api/auth/login', payload).then((response) => response.data),
  register: (payload) => http.post('/api/auth/register', payload).then((response) => response.data),
  me: () => http.get('/api/auth/me').then((response) => response.data),

  agents: () => http.get('/api/agents').then((response) => response.data),

  createConversation: (agentId) =>
    http.post('/api/conversations', { agentId }).then((response) => response.data),
  myConversations: () => http.get('/api/conversations/mine').then((response) => response.data),
  closeConversation: (id) =>
    http.post(`/api/conversations/${id}/close`).then((response) => response.data),
  messages: (id, afterSeq, limit) =>
    http
      .get(`/api/conversations/${id}/messages`, { params: { afterSeq, limit } })
      .then((response) => response.data),

  tickets: () => http.get('/api/tickets').then((response) => response.data),
  ticketMeta: () => http.get('/api/tickets/meta').then((response) => response.data),
  ticket: (id) => http.get(`/api/tickets/${id}`).then((response) => response.data),
  transferTicket: (id, payload) =>
    http.post(`/api/tickets/${id}/transfer`, payload).then((response) => response.data),
  exportTicket: (id, format) =>
    http
      .get(`/api/tickets/${id}/export`, { params: { format }, responseType: 'blob' })
      .then((response) => response.data),

  /** 上传图片或附件，返回 { id, url, name, size, contentType }。 */
  uploadFile: (file) => {
    const form = new FormData()
    form.append('file', file)
    return http
      .post('/api/files', form, { headers: { 'Content-Type': 'multipart/form-data' } })
      .then((response) => response.data)
  },

  // 管理中台（仅 admin 角色可调）
  adminAgents: () => http.get('/api/admin/agents').then((response) => response.data),
  adminCreateAgent: (payload) =>
    http.post('/api/admin/agents', payload).then((response) => response.data),
  adminUpdateAgent: (id, payload) =>
    http.patch(`/api/admin/agents/${id}`, payload).then((response) => response.data),
  adminConversations: (params) =>
    http.get('/api/admin/conversations', { params }).then((response) => response.data),
  adminTickets: (params) =>
    http.get('/api/admin/tickets', { params }).then((response) => response.data),
  adminTicket: (id) => http.get(`/api/admin/tickets/${id}`).then((response) => response.data),
  adminTransferTicket: (id, payload) =>
    http.post(`/api/admin/tickets/${id}/transfer`, payload).then((response) => response.data),
  adminStats: (days) =>
    http.get('/api/admin/stats', { params: { days } }).then((response) => response.data)
}
