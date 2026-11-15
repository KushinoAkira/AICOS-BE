import assert from 'node:assert/strict';
export async function precheckContracts(c,f){
 const {request,branch,cashier,cashier2,barista,owner,open,add,bill,collect}=f;
 const url='/api/v1/payments/cashier-shifts/cash-precheck?branchId='+branch.id;
 const own=(await request('GET',url,cashier,null,null,200)).data;assert.equal(own.allowed,true);assert.equal(own.requiresOpenShift,c.requireShift);
 const missing=(await request('GET',url,cashier2,null,null,200)).data;assert.equal(missing.allowed,!c.requireShift);
 await request('GET',url,null,null,null,401);await request('GET',url,barista,null,null,403);
 const session=await open('AUTH-CHECK');await add(session);const draft=await bill(session);
 const me=(await request('GET','/api/v1/identity/auth/me',cashier2,null,null,200)).data;
 const profile={fullName:me.fullName,email:me.email,username:me.username,phone:me.phone,role:me.role,branchId:me.branchId};
 await request('PUT','/api/v1/identity/users/'+me.id,owner,{...profile,role:'Barista'},null,200);
 await request('GET',url,cashier2,null,null,403);assert.equal((await collect(draft,cashier2)).status,403);
 const foreign=(await request('POST','/api/v1/identity/branches',owner,{name:'Precheck other branch',address:'Test',phone:'0900000003',openingTime:'00:00:00',closingTime:'23:59:00'},null,201)).data;
 await request('PUT','/api/v1/identity/users/'+me.id,owner,{...profile,branchId:foreign.id},null,200);
 await request('GET',url,cashier2,null,null,403);assert.equal((await collect(draft,cashier2)).status,403);
 await request('PUT','/api/v1/identity/users/'+me.id,owner,profile,null,200);
 const attempts=await c.sql(c.databases.payment,"SELECT count(*) FROM payment.session_bill_attempts WHERE bill_id='"+draft.id+"';");assert.equal(attempts,'0');
 c.report('Real Identity role revocation/branch reassignment reject old JWT for precheck AND Cash before any financial attempt');
 c.report('Precheck own OPEN/missing shift policy, anonymous 401 and non-counter 403 through Gateway');
}
